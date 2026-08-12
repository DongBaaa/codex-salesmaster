using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
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
    [InlineData(true)]
    [InlineData(false)]
    public async Task RuntimeScopedSync_SiblingUiEditBeforeOrDuringPush_RemainsDirtyAfterLaterSave(
        bool editBeforePush)
    {
        PrepareAppRoot($"georaeplan-runtime-sibling-ui-edit-{editBeforePush}");

        try
        {
            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var newerUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const string originalName = "runtime sibling prepared unit";
            const string newerName = "runtime sibling newer UI payload";
            var handler = new DelayedPushAckThenEmptyPullHandler(
                unitId,
                entityName: "Unit",
                acceptedRevision: 12,
                acceptedUpdatedAtUtc: originalUpdatedAtUtc.AddMinutes(1));

            await using var provider = BuildRuntimeProvider(session, handler);
            await using var uiScope = provider.CreateAsyncScope();
            var uiDb = uiScope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await uiDb.Database.EnsureDeletedAsync();
            await uiDb.Database.EnsureCreatedAsync();
            uiDb.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = originalName,
                IsActive = true,
                Revision = 11,
                IsDirty = true,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            uiDb.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await uiDb.SaveChangesAsync();
            uiDb.ChangeTracker.Clear();

            var trackedUnit = await uiDb.Units.IgnoreQueryFilters()
                .SingleAsync(unit => unit.Id == unitId);
            void EditTrackedUnit()
            {
                trackedUnit.Name = newerName;
                trackedUnit.UpdatedAtUtc = newerUpdatedAtUtc;
                uiDb.ChangeTracker.DetectChanges();
                Assert.Equal(EntityState.Modified, uiDb.Entry(trackedUnit).State);
                Assert.False(uiDb.Entry(trackedUnit)
                    .Property(unit => unit.IsDirty)
                    .IsModified);
            }

            if (editBeforePush)
                EditTrackedUnit();

            await using var runtimeScope = provider.CreateAsyncScope();
            var runtimeSync =
                runtimeScope.ServiceProvider.GetRequiredService<SyncService>();
            var syncTask = runtimeSync.TrySyncAsync();
            try
            {
                await handler.PushReceived.Task.WaitAsync(
                    TimeSpan.FromSeconds(15));
                if (!editBeforePush)
                    EditTrackedUnit();
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.True(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            await using (var beforeUiSaveDb = new LocalDbContext())
            {
                var acknowledged = await beforeUiSaveDb.Units
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(unit => unit.Id == unitId);
                Assert.Equal(originalName, acknowledged.Name);
                Assert.False(acknowledged.IsDirty);
                Assert.Equal(12, acknowledged.Revision);
            }

            await uiDb.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.Units
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(newerName, saved.Name);
            Assert.Equal(newerUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.True(saved.IsDirty);
            Assert.Equal(12, saved.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RuntimeScopedSync_SavedPayloadDuringServerNewerConflict_RebasesAndRetriesPayload()
    {
        PrepareAppRoot("georaeplan-runtime-saved-conflict-retry");

        try
        {
            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var newerUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const string originalName = "conflict payload A";
            const string newerName = "conflict payload B";
            var handler = new DelayedServerNewerConflictThenAckHandler(
                unitId,
                serverRevision: 12,
                acceptedRevision: 13,
                acceptedUpdatedAtUtc: newerUpdatedAtUtc.AddMinutes(1));

            await using var provider = BuildRuntimeProvider(session, handler);
            await using var uiScope = provider.CreateAsyncScope();
            var uiDb = uiScope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await uiDb.Database.EnsureDeletedAsync();
            await uiDb.Database.EnsureCreatedAsync();
            uiDb.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = originalName,
                IsActive = true,
                Revision = 11,
                IsDirty = true,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            uiDb.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await uiDb.SaveChangesAsync();
            uiDb.ChangeTracker.Clear();

            var trackedUnit = await uiDb.Units.IgnoreQueryFilters()
                .SingleAsync(unit => unit.Id == unitId);
            await using var runtimeScope = provider.CreateAsyncScope();
            var runtimeSync =
                runtimeScope.ServiceProvider.GetRequiredService<SyncService>();
            var firstSync = runtimeSync.TrySyncAsync();
            await handler.FirstPushReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(15));
            try
            {
                trackedUnit.Name = newerName;
                trackedUnit.UpdatedAtUtc = newerUpdatedAtUtc;
                trackedUnit.IsDirty = true;
                await uiDb.SaveChangesAsync();
                Assert.Equal(EntityState.Unchanged, uiDb.Entry(trackedUnit).State);
            }
            finally
            {
                handler.ReleaseFirstPush();
            }

            Assert.False(await firstSync.WaitAsync(TimeSpan.FromSeconds(15)));

            await using (var afterConflictDb = new LocalDbContext())
            {
                var rebased = await afterConflictDb.Units
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(unit => unit.Id == unitId);
                Assert.Equal(newerName, rebased.Name);
                Assert.Equal(newerUpdatedAtUtc, rebased.UpdatedAtUtc);
                Assert.True(rebased.IsDirty);
                Assert.Equal(12, rebased.Revision);
            }

            uiDb.ChangeTracker.Clear();
            Assert.True(
                await runtimeSync.TrySyncAsync()
                    .WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(2, handler.PushRequests.Count);
            Assert.Equal(
                originalName,
                Assert.Single(handler.PushRequests[0].Units).Name);
            var retriedUnit = Assert.Single(handler.PushRequests[1].Units);
            Assert.Equal(newerName, retriedUnit.Name);
            Assert.Equal(12, retriedUnit.ExpectedRevision);

            await using var verificationDb = new LocalDbContext();
            var accepted = await verificationDb.Units
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(newerName, accepted.Name);
            Assert.False(accepted.IsDirty);
            Assert.Equal(13, accepted.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RuntimeScopedSync_ExactPreparedServerNewerConflict_RemainsDirtyWithoutTypedAtomicReceipt()
    {
        PrepareAppRoot("georaeplan-runtime-exact-server-newer");

        try
        {
            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var updatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            const string preparedName = "exact prepared payload A";
            var handler = new DelayedServerNewerConflictThenAckHandler(
                unitId,
                serverRevision: 12,
                acceptedRevision: 12,
                acceptedUpdatedAtUtc: updatedAtUtc.AddMinutes(1));

            await using var provider = BuildRuntimeProvider(session, handler);
            await using (var seedScope = provider.CreateAsyncScope())
            {
                var seedDb =
                    seedScope.ServiceProvider.GetRequiredService<LocalDbContext>();
                await seedDb.Database.EnsureDeletedAsync();
                await seedDb.Database.EnsureCreatedAsync();
                seedDb.Units.Add(new LocalUnit
                {
                    Id = unitId,
                    Name = preparedName,
                    IsActive = true,
                    Revision = 11,
                    IsDirty = true,
                    CreatedAtUtc = updatedAtUtc.AddHours(-1),
                    UpdatedAtUtc = updatedAtUtc
                });
                seedDb.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await seedDb.SaveChangesAsync();
            }

            await using var runtimeScope = provider.CreateAsyncScope();
            var runtimeSync =
                runtimeScope.ServiceProvider.GetRequiredService<SyncService>();
            var syncTask = runtimeSync.TrySyncAsync();
            await handler.FirstPushReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(15));
            handler.ReleaseFirstPush();

            Assert.False(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Single(handler.PushRequests);
            var pushedUnit = Assert.Single(handler.PushRequests[0].Units);
            Assert.Equal(preparedName, pushedUnit.Name);
            Assert.Equal(11, pushedUnit.ExpectedRevision);

            await using var verificationDb = new LocalDbContext();
            var resolved = await verificationDb.Units
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(preparedName, resolved.Name);
            Assert.True(resolved.IsDirty);
            Assert.Equal(12, resolved.Revision);
            Assert.NotEqual("Acknowledged", await verificationDb.SyncOutboxEntries
                .AsNoTracking().Select(entry => entry.Status).SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("revision-only")]
    [InlineData("user")]
    [InlineData("session")]
    [InlineData("device")]
    [InlineData("database")]
    [InlineData("tenant")]
    [InlineData("office")]
    [InlineData("responsible-office")]
    [InlineData("expected-revision")]
    public async Task RuntimeScopedSync_ServerNewerConflictWithoutExactSnapshotAndAnchorProofStaysDirtyAndPending(
        string mismatch)
    {
        PrepareAppRoot($"georaeplan-runtime-server-newer-proof-{mismatch}");

        try
        {
            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var updatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var handler = new DelayedServerNewerConflictThenAckHandler(
                unitId,
                serverRevision: 12,
                acceptedRevision: 12,
                acceptedUpdatedAtUtc: updatedAtUtc.AddMinutes(1),
                includeFullServerSnapshot: mismatch != "revision-only");

            await using var provider = BuildRuntimeProvider(session, handler);
            await using (var seedScope = provider.CreateAsyncScope())
            {
                var seedDb = seedScope.ServiceProvider.GetRequiredService<LocalDbContext>();
                await seedDb.Database.EnsureDeletedAsync();
                await seedDb.Database.EnsureCreatedAsync();
                seedDb.Units.Add(new LocalUnit
                {
                    Id = unitId,
                    Name = "must remain pending",
                    IsActive = true,
                    Revision = 11,
                    IsDirty = true,
                    CreatedAtUtc = updatedAtUtc.AddHours(-1),
                    UpdatedAtUtc = updatedAtUtc
                });
                seedDb.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await seedDb.SaveChangesAsync();
            }

            await using var runtimeScope = provider.CreateAsyncScope();
            var runtimeSync = runtimeScope.ServiceProvider.GetRequiredService<SyncService>();
            var syncTask = runtimeSync.TrySyncAsync();
            await handler.FirstPushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (mismatch != "revision-only")
            {
                await using var mutationDb = new LocalDbContext();
                var outbox = await mutationDb.SyncOutboxEntries.SingleAsync();
                switch (mismatch)
                {
                    case "user":
                        outbox.UserId = Guid.NewGuid();
                        break;
                    case "session":
                        outbox.SessionId = Guid.NewGuid();
                        break;
                    case "device":
                        outbox.DeviceId = "other-device";
                        break;
                    case "database":
                        outbox.BusinessDatabaseName = "ITWORLD";
                        break;
                    case "tenant":
                        outbox.TenantCode = TenantScopeCatalog.Itworld;
                        break;
                    case "office":
                        outbox.OfficeCode = OfficeCodeCatalog.Itworld;
                        break;
                    case "responsible-office":
                        outbox.ResponsibleOfficeCode = OfficeCodeCatalog.Itworld;
                        break;
                    case "expected-revision":
                        outbox.ExpectedRevision = 10;
                        break;
                }
                await mutationDb.SaveChangesAsync();
            }
            handler.ReleaseFirstPush();

            Assert.False(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));
            await using var verificationDb = new LocalDbContext();
            var preserved = await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking().SingleAsync(unit => unit.Id == unitId);
            Assert.Equal("must remain pending", preserved.Name);
            Assert.True(preserved.IsDirty);
            Assert.Equal(
                mismatch == "revision-only" ? 12 : 11,
                preserved.Revision);
            Assert.NotEqual("Acknowledged", await verificationDb.SyncOutboxEntries
                .AsNoTracking().Select(entry => entry.Status).SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RuntimeScopedSync_LineEditDuringServerNewerConflictPreservesDirtyPayload()
    {
        PrepareAppRoot("georaeplan-runtime-line-toctou");

        try
        {
            var session = CreateAdminSession();
            var transferId = Guid.NewGuid();
            var lineId = Guid.NewGuid();
            var updatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            const string preparedRemark = "prepared line A";
            const string newerRemark = "concurrent UI line B";
            var handler = new InventoryTransferServerNewerConflictHandler(
                transferId,
                serverRevision: 42);

            await using var provider = BuildRuntimeProvider(session, handler);
            await using var uiScope = provider.CreateAsyncScope();
            var uiDb = uiScope.ServiceProvider
                .GetRequiredService<LocalDbContext>();
            await uiDb.Database.EnsureDeletedAsync();
            await uiDb.Database.EnsureCreatedAsync();
            uiDb.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "TR-LINE-TOCTOU",
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                CreatedAtUtc = updatedAtUtc.AddHours(-1),
                UpdatedAtUtc = updatedAtUtc,
                Revision = 41,
                IsDirty = true,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = lineId,
                        TransferId = transferId,
                        ItemNameOriginal = "TOCTOU item",
                        Unit = "EA",
                        Quantity = 1m,
                        ReceiptRemark = preparedRemark
                    }
                ]
            });
            uiDb.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await uiDb.SaveChangesAsync();
            uiDb.ChangeTracker.Clear();

            var trackedTransfer = await uiDb.InventoryTransfers
                .IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .SingleAsync(transfer => transfer.Id == transferId);
            var trackedLine = Assert.Single(trackedTransfer.Lines);

            await using var runtimeScope = provider.CreateAsyncScope();
            var runtimeSync =
                runtimeScope.ServiceProvider.GetRequiredService<SyncService>();
            var syncTask = runtimeSync.TrySyncAsync();
            await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            trackedLine.ReceiptRemark = newerRemark;
            uiDb.ChangeTracker.DetectChanges();
            Assert.Equal(
                EntityState.Unchanged,
                uiDb.Entry(trackedTransfer).State);
            Assert.Equal(
                EntityState.Modified,
                uiDb.Entry(trackedLine).State);
            await uiDb.SaveChangesAsync();
            Assert.True(trackedTransfer.UpdatedAtUtc > updatedAtUtc);

            await using (var beforeCleanDb = new LocalDbContext())
            {
                var beforeClean = await beforeCleanDb.InventoryTransfers
                    .IgnoreQueryFilters()
                    .Include(transfer => transfer.Lines)
                    .AsNoTracking()
                    .SingleAsync(transfer => transfer.Id == transferId);
                Assert.True(beforeClean.IsDirty);
                Assert.Equal(41, beforeClean.Revision);
                Assert.True(beforeClean.UpdatedAtUtc > updatedAtUtc);
                Assert.Equal(
                    newerRemark,
                    Assert.Single(beforeClean.Lines).ReceiptRemark);
            }

            handler.ReleasePush();
            Assert.False(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Single(handler.PushRequests);
            var pushedTransfer = Assert.Single(
                handler.PushRequests[0].InventoryTransfers);
            Assert.Equal(41, pushedTransfer.ExpectedRevision);
            Assert.Equal(
                preparedRemark,
                Assert.Single(pushedTransfer.Lines).ReceiptRemark);
            Assert.Equal(1, handler.PullCount);

            await using var verificationDb = new LocalDbContext();
            var preserved = await verificationDb.InventoryTransfers
                .IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .AsNoTracking()
                .SingleAsync(transfer => transfer.Id == transferId);
            Assert.True(preserved.IsDirty);
            Assert.Equal(42, preserved.Revision);
            Assert.True(preserved.UpdatedAtUtc > updatedAtUtc);
            Assert.Equal(
                newerRemark,
                Assert.Single(preserved.Lines).ReceiptRemark);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RuntimeScopedSync_LineEditAfterExactAcceptedRead_RebasesThenRetries()
    {
        PrepareAppRoot("georaeplan-runtime-accepted-line-toctou");

        try
        {
            var session = CreateAdminSession();
            var transferId = Guid.NewGuid();
            var lineId = Guid.NewGuid();
            var updatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            const string preparedRemark = "accepted prepared line A";
            const string newerRemark = "accepted concurrent UI line B";
            var handler = new InventoryTransferAcceptedThenEmptyPullHandler(
                transferId,
                firstAcceptedRevision: 42,
                secondAcceptedRevision: 43);

            await using var provider = BuildRuntimeProvider(session, handler);
            await using var uiScope = provider.CreateAsyncScope();
            var uiDb = uiScope.ServiceProvider
                .GetRequiredService<LocalDbContext>();
            await uiDb.Database.EnsureDeletedAsync();
            await uiDb.Database.EnsureCreatedAsync();
            uiDb.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "TR-ACCEPTED-LINE-TOCTOU",
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                CreatedAtUtc = updatedAtUtc.AddHours(-1),
                UpdatedAtUtc = updatedAtUtc,
                Revision = 41,
                IsDirty = true,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = lineId,
                        TransferId = transferId,
                        ItemNameOriginal = "accepted TOCTOU item",
                        Unit = "EA",
                        Quantity = 1m,
                        ReceiptRemark = preparedRemark
                    }
                ]
            });
            uiDb.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await uiDb.SaveChangesAsync();
            uiDb.ChangeTracker.Clear();

            var trackedTransfer = await uiDb.InventoryTransfers
                .IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .SingleAsync(transfer => transfer.Id == transferId);
            var trackedLine = Assert.Single(trackedTransfer.Lines);

            await using var runtimeScope = provider.CreateAsyncScope();
            var runtimeSync =
                runtimeScope.ServiceProvider.GetRequiredService<SyncService>();
            var exactReadReached =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseConditionalClean =
                new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var hookCallCount = 0;
            var affectedRows = new List<int>();
            runtimeSync.BeforeAcceptedRevisionCleanAsyncForTesting =
                async ct =>
                {
                    if (Interlocked.Increment(ref hookCallCount) != 1)
                        return;

                    exactReadReached.TrySetResult(true);
                    await releaseConditionalClean.Task.WaitAsync(
                        TimeSpan.FromSeconds(15),
                        ct);
                };
            runtimeSync.AcceptedRevisionCleanAffectedRowsForTesting =
                affected => affectedRows.Add(affected);

            var firstSync = runtimeSync.TrySyncAsync();
            await exactReadReached.Task.WaitAsync(TimeSpan.FromSeconds(15));
            trackedLine.ReceiptRemark = newerRemark;
            uiDb.ChangeTracker.DetectChanges();
            Assert.Equal(
                EntityState.Unchanged,
                uiDb.Entry(trackedTransfer).State);
            Assert.Equal(
                EntityState.Modified,
                uiDb.Entry(trackedLine).State);
            await uiDb.SaveChangesAsync();
            Assert.True(trackedTransfer.UpdatedAtUtc > updatedAtUtc);

            releaseConditionalClean.TrySetResult(true);
            Assert.True(await firstSync.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal([0], affectedRows);

            await using (var afterFirstAckDb = new LocalDbContext())
            {
                var preserved = await afterFirstAckDb.InventoryTransfers
                    .IgnoreQueryFilters()
                    .Include(transfer => transfer.Lines)
                    .AsNoTracking()
                    .SingleAsync(transfer => transfer.Id == transferId);
                Assert.True(preserved.IsDirty);
                Assert.Equal(42, preserved.Revision);
                Assert.True(preserved.UpdatedAtUtc > updatedAtUtc);
                Assert.Equal(
                    newerRemark,
                    Assert.Single(preserved.Lines).ReceiptRemark);
            }

            uiDb.ChangeTracker.Clear();
            Assert.True(
                await runtimeSync.TrySyncAsync()
                    .WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal([0, 1], affectedRows);
            Assert.Equal(2, handler.PushRequests.Count);
            var firstPushed = Assert.Single(
                handler.PushRequests[0].InventoryTransfers);
            Assert.Equal(41, firstPushed.ExpectedRevision);
            Assert.Equal(
                preparedRemark,
                Assert.Single(firstPushed.Lines).ReceiptRemark);
            var secondPushed = Assert.Single(
                handler.PushRequests[1].InventoryTransfers);
            Assert.Equal(42, secondPushed.ExpectedRevision);
            Assert.Equal(
                newerRemark,
                Assert.Single(secondPushed.Lines).ReceiptRemark);

            await using var verificationDb = new LocalDbContext();
            var accepted = await verificationDb.InventoryTransfers
                .IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .AsNoTracking()
                .SingleAsync(transfer => transfer.Id == transferId);
            Assert.False(accepted.IsDirty);
            Assert.Equal(43, accepted.Revision);
            Assert.Equal(
                newerRemark,
                Assert.Single(accepted.Lines).ReceiptRemark);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RuntimeScopedSync_InventoryTransferStockAtomicityRollback_BlocksOnlyTransferScopeOnRetry()
    {
        PrepareAppRoot(
            "georaeplan-runtime-transfer-stock-atomicity-rollback");

        try
        {
            var session = CreateAdminSession();
            var transferId = Guid.NewGuid();
            var transferLineId = Guid.NewGuid();
            var blockedItemId = Guid.NewGuid();
            var unrelatedItemId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            var handler =
                new InventoryTransferStockAtomicityRollbackThenAcceptHandler(
                    transferId,
                    blockedItemId,
                    unrelatedItemId,
                    acceptedItemRevision: 22);

            await using var provider = BuildRuntimeProvider(session, handler);
            await using var setupScope = provider.CreateAsyncScope();
            var setupDb =
                setupScope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await setupDb.Database.EnsureDeletedAsync();
            await setupDb.Database.EnsureCreatedAsync();
            setupDb.Items.AddRange(
                new LocalItem
                {
                    Id = blockedItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "atomic rollback transfer item",
                    NameMatchKey = "atomic rollback transfer item",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    CurrentStock = 10m,
                    Revision = 11,
                    IsDirty = true,
                    CreatedAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now
                },
                new LocalItem
                {
                    Id = unrelatedItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "atomic rollback unrelated item",
                    NameMatchKey = "atomic rollback unrelated item",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    CurrentStock = 4m,
                    Revision = 21,
                    IsDirty = true,
                    CreatedAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now
                });
            setupDb.ItemWarehouseStocks.AddRange(
                new LocalItemWarehouseStock
                {
                    ItemId = blockedItemId,
                    WarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 10m,
                    Revision = 31,
                    UpdatedAtUtc = now
                },
                new LocalItemWarehouseStock
                {
                    ItemId = unrelatedItemId,
                    WarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 4m,
                    Revision = 41,
                    UpdatedAtUtc = now
                });
            setupDb.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "TR-STOCK-ATOMICITY-ROLLBACK",
                FromWarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode =
                    OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferStatus = "수령대기",
                CreatedByUsername = session.User!.Username,
                LastSavedByUsername = session.User.Username,
                Revision = 7,
                IsDirty = true,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = transferLineId,
                        TransferId = transferId,
                        ItemId = blockedItemId,
                        ItemNameOriginal =
                            "atomic rollback transfer item",
                        Unit = "EA",
                        Quantity = 2m
                    }
                ]
            });
            setupDb.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await setupDb.SaveChangesAsync();
            setupDb.ChangeTracker.Clear();

            await using var runtimeScope = provider.CreateAsyncScope();
            var sync =
                runtimeScope.ServiceProvider.GetRequiredService<SyncService>();

            Assert.True(
                await sync.TrySyncAsync()
                    .WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Single(handler.PushRequests);
            var pullCountAfterRollback = handler.PullCount;
            Assert.True(pullCountAfterRollback > 0);

            await using (var afterRollbackDb = new LocalDbContext())
            {
                var transfer = await afterRollbackDb.InventoryTransfers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(entity => entity.Id == transferId);
                var blockedItem = await afterRollbackDb.Items
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(entity => entity.Id == blockedItemId);
                var unrelatedItem = await afterRollbackDb.Items
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(entity => entity.Id == unrelatedItemId);
                var stocks = await afterRollbackDb.ItemWarehouseStocks
                    .AsNoTracking()
                    .OrderBy(stock => stock.ItemId)
                    .ToListAsync();

                Assert.True(transfer.IsDirty);
                Assert.Equal(7, transfer.Revision);
                Assert.True(blockedItem.IsDirty);
                Assert.Equal(11, blockedItem.Revision);
                Assert.True(unrelatedItem.IsDirty);
                Assert.Equal(21, unrelatedItem.Revision);
                Assert.Contains(
                    stocks,
                    stock =>
                        stock.ItemId == blockedItemId &&
                        stock.Quantity == 10m &&
                        stock.Revision == 31);
                Assert.Contains(
                    stocks,
                    stock =>
                        stock.ItemId == unrelatedItemId &&
                        stock.Quantity == 4m &&
                        stock.Revision == 41);

                var outbox = await afterRollbackDb.SyncOutboxEntries
                    .AsNoTracking()
                    .ToListAsync();
                var failed = Assert.Single(
                    outbox,
                    entry => entry.Status == "Failed");
                Assert.Equal(
                    nameof(LocalInventoryTransfer),
                    failed.EntityName);
                Assert.Equal(transferId, failed.EntityId);
                Assert.StartsWith(
                    "[inventory-transfer-stock-atomicity-rollback]",
                    failed.ErrorMessage,
                    StringComparison.Ordinal);
                Assert.All(
                    outbox.Where(entry => entry.Id != failed.Id),
                    entry => Assert.NotEqual("Failed", entry.Status));
            }

            Assert.True(
                await sync.TrySyncAsync()
                    .WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(2, handler.PushRequests.Count);
            Assert.True(handler.PullCount > pullCountAfterRollback);

            var retry = handler.PushRequests[1];
            Assert.Empty(retry.InventoryTransfers);
            Assert.DoesNotContain(
                retry.Items,
                item => item.Id == blockedItemId);
            Assert.DoesNotContain(
                retry.ItemWarehouseStocks,
                stock => stock.ItemId == blockedItemId);
            Assert.Single(
                retry.Items,
                item => item.Id == unrelatedItemId);
            Assert.Single(
                retry.ItemWarehouseStocks,
                stock => stock.ItemId == unrelatedItemId);

            await using var verificationDb = new LocalDbContext();
            var preservedTransfer = await verificationDb.InventoryTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(entity => entity.Id == transferId);
            var preservedBlockedItem = await verificationDb.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(entity => entity.Id == blockedItemId);
            var acceptedUnrelatedItem = await verificationDb.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(entity => entity.Id == unrelatedItemId);
            Assert.True(preservedTransfer.IsDirty);
            Assert.Equal(7, preservedTransfer.Revision);
            Assert.True(preservedBlockedItem.IsDirty);
            Assert.Equal(11, preservedBlockedItem.Revision);
            Assert.False(acceptedUnrelatedItem.IsDirty);
            Assert.Equal(22, acceptedUnrelatedItem.Revision);

            var finalOutbox = await verificationDb.SyncOutboxEntries
                .AsNoTracking()
                .ToListAsync();
            var transferOutbox = Assert.Single(
                finalOutbox,
                entry =>
                    entry.EntityName ==
                    nameof(LocalInventoryTransfer));
            Assert.Equal("Failed", transferOutbox.Status);
            Assert.StartsWith(
                "[inventory-transfer-stock-atomicity-rollback]",
                transferOutbox.ErrorMessage,
                StringComparison.Ordinal);
            var unrelatedItemOutbox = Assert.Single(
                finalOutbox,
                entry =>
                    entry.EntityName == nameof(LocalItem) &&
                    entry.EntityId == unrelatedItemId);
            Assert.Equal("Acknowledged", unrelatedItemOutbox.Status);
            Assert.Equal(22, unrelatedItemOutbox.AcceptedRevision);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RuntimeScopedSync_InventoryTransferStockAtomicityRollback_UserEditCreatesNewMutationAndConvergesClean()
    {
        PrepareAppRoot(
            "georaeplan-runtime-transfer-stock-atomicity-user-edit-retry");

        try
        {
            var session = CreateAdminSession();
            var transferId = Guid.NewGuid();
            var transferLineId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            var handler =
                new InventoryTransferStockAtomicityRollbackThenAcceptEditedHandler(
                    transferId,
                    itemId,
                    acceptedTransferRevision: 8,
                    acceptedItemRevision: 12);

            await using var provider = BuildRuntimeProvider(session, handler);
            await using (var setupScope = provider.CreateAsyncScope())
            {
                var setupDb =
                    setupScope.ServiceProvider
                        .GetRequiredService<LocalDbContext>();
                await setupDb.Database.EnsureDeletedAsync();
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Items.Add(new LocalItem
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "atomic rollback editable item",
                    NameMatchKey = "atomic rollback editable item",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    CurrentStock = 10m,
                    Revision = 11,
                    IsDirty = true,
                    CreatedAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now
                });
                setupDb.ItemWarehouseStocks.Add(
                    new LocalItemWarehouseStock
                    {
                        ItemId = itemId,
                        WarehouseCode =
                            OfficeCodeCatalog.UsenetMainWarehouse,
                        Quantity = 10m,
                        Revision = 31,
                        UpdatedAtUtc = now
                    });
                setupDb.InventoryTransfers.Add(
                    new LocalInventoryTransfer
                    {
                        Id = transferId,
                        TransferNumber = "TR-STOCK-ATOMICITY-EDIT-RETRY",
                        TransferDate = DateOnly.FromDateTime(now),
                        FromWarehouseCode =
                            OfficeCodeCatalog.UsenetMainWarehouse,
                        ToWarehouseCode =
                            OfficeCodeCatalog.YeonsuMainWarehouse,
                        TransferStatus = "수령대기",
                        Memo = "server-rejected payload",
                        CreatedByUsername = session.User!.Username,
                        LastSavedByUsername = session.User.Username,
                        Revision = 7,
                        IsDirty = true,
                        CreatedAtUtc = now.AddHours(-1),
                        UpdatedAtUtc = now,
                        Lines =
                        [
                            new LocalInventoryTransferLine
                            {
                                Id = transferLineId,
                                TransferId = transferId,
                                ItemId = itemId,
                                ItemNameOriginal =
                                    "atomic rollback editable item",
                                Unit = "EA",
                                Quantity = 2m
                            }
                        ]
                    });
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await setupDb.SaveChangesAsync();
            }

            await using (var firstSyncScope = provider.CreateAsyncScope())
            {
                var firstSync =
                    firstSyncScope.ServiceProvider
                        .GetRequiredService<SyncService>();
                Assert.True(
                    await firstSync.TrySyncAsync()
                        .WaitAsync(TimeSpan.FromSeconds(15)));
            }

            Assert.Single(handler.PushRequests);
            var firstPushedTransfer = Assert.Single(
                handler.PushRequests[0].InventoryTransfers);
            var rejectedMutationId = firstPushedTransfer.MutationId;
            Assert.False(string.IsNullOrWhiteSpace(rejectedMutationId));

            await using (var afterRollbackDb = new LocalDbContext())
            {
                var failed = Assert.Single(
                    await afterRollbackDb.SyncOutboxEntries
                        .AsNoTracking()
                        .Where(entry =>
                            entry.EntityName ==
                                nameof(LocalInventoryTransfer) &&
                            entry.EntityId == transferId)
                        .ToListAsync());
                Assert.Equal("Failed", failed.Status);
                Assert.Equal(rejectedMutationId, failed.MutationId);
                Assert.StartsWith(
                    "[inventory-transfer-stock-atomicity-rollback]",
                    failed.ErrorMessage,
                    StringComparison.Ordinal);
            }

            await using (var editScope = provider.CreateAsyncScope())
            {
                var local =
                    editScope.ServiceProvider
                        .GetRequiredService<LocalStateService>();
                var edited = await local.GetInventoryTransferAsync(
                    transferId,
                    session);
                Assert.NotNull(edited);
                edited!.Memo = "user-corrected payload";
                var saveResult = await local.SaveInventoryTransferAsync(
                    edited,
                    session);
                Assert.True(saveResult.Success, saveResult.Message);
            }

            await using (var secondSyncScope = provider.CreateAsyncScope())
            {
                var secondSync =
                    secondSyncScope.ServiceProvider
                        .GetRequiredService<SyncService>();
                Assert.True(
                    await secondSync.TrySyncAsync()
                        .WaitAsync(TimeSpan.FromSeconds(15)));
            }

            Assert.Equal(2, handler.PushRequests.Count);
            var retriedTransfer = Assert.Single(
                handler.PushRequests[1].InventoryTransfers);
            Assert.Equal(transferId, retriedTransfer.Id);
            Assert.Equal("user-corrected payload", retriedTransfer.Memo);
            Assert.False(
                string.Equals(
                    rejectedMutationId,
                    retriedTransfer.MutationId,
                    StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                handler.PushRequests[1].Items,
                item => item.Id == itemId);
            Assert.Contains(
                handler.PushRequests[1].ItemWarehouseStocks,
                stock => stock.ItemId == itemId);

            await using var verificationDb = new LocalDbContext();
            var acceptedTransfer = await verificationDb.InventoryTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(entity => entity.Id == transferId);
            var acceptedItem = await verificationDb.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(entity => entity.Id == itemId);
            Assert.False(acceptedTransfer.IsDirty);
            Assert.Equal(8, acceptedTransfer.Revision);
            Assert.Equal("user-corrected payload", acceptedTransfer.Memo);
            Assert.False(acceptedItem.IsDirty);
            Assert.Equal(12, acceptedItem.Revision);

            var transferOutbox = await verificationDb.SyncOutboxEntries
                .AsNoTracking()
                .Where(entry =>
                    entry.EntityName == nameof(LocalInventoryTransfer) &&
                    entry.EntityId == transferId)
                .OrderBy(entry => entry.PreparedAtUtc)
                .ToListAsync();
            Assert.Equal(2, transferOutbox.Count);
            Assert.Contains(
                transferOutbox,
                entry =>
                    entry.MutationId == rejectedMutationId &&
                    entry.Status == "Acknowledged");
            Assert.Contains(
                transferOutbox,
                entry =>
                    entry.MutationId == retriedTransfer.MutationId &&
                    entry.Status == "Acknowledged" &&
                    entry.AcceptedRevision == 8);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("rollback")]
    [InlineData("owner-aba")]
    [InlineData("missing-receipt")]
    [InlineData("concurrent-edit")]
    public async Task RuntimeScopedSync_PurgedTransferAcknowledgement_IsAtomicWhenCursorIsAhead(
        string scenario)
    {
        PrepareAppRoot(
            $"georaeplan-runtime-purged-transfer-acknowledgement-{scenario}");

        try
        {
            var session = CreateAdminSession();
            var transferId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-10);
            const long purgeRevision = 40;
            const long pullCursor = 100;
            var handler = new InventoryTransferPurgedNoopHandler(
                transferId,
                receiptId,
                purgeRevision,
                pullCursor,
                now.AddMinutes(5),
                delayPushResponse: string.Equals(
                    scenario,
                    "owner-aba",
                    StringComparison.Ordinal) || string.Equals(
                    scenario,
                    "concurrent-edit",
                    StringComparison.Ordinal),
                includePurgeReceipt: !string.Equals(
                    scenario,
                    "missing-receipt",
                    StringComparison.Ordinal));

            await using var provider = BuildRuntimeProvider(session, handler);
            var evidencePath = Path.Combine(
                AppPaths.TransactionAttachmentsDir,
                $"purged-transfer-{transferId:N}.pdf");
            Directory.CreateDirectory(AppPaths.TransactionAttachmentsDir);
            await File.WriteAllTextAsync(
                evidencePath,
                "purged transfer evidence");
            await using (var setupScope = provider.CreateAsyncScope())
            {
                var setupDb = setupScope.ServiceProvider
                    .GetRequiredService<LocalDbContext>();
                await setupDb.Database.EnsureDeletedAsync();
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Items.Add(new LocalItem
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "purged transfer stock item",
                    NameMatchKey = "purged transfer stock item",
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
                        WarehouseCode =
                            OfficeCodeCatalog.UsenetMainWarehouse,
                        Quantity = -2m,
                        Revision = 20,
                        UpdatedAtUtc = now
                    },
                    new LocalItemWarehouseStock
                    {
                        ItemId = itemId,
                        WarehouseCode =
                            OfficeCodeCatalog.YeonsuMainWarehouse,
                        Quantity = 2m,
                        Revision = 21,
                        UpdatedAtUtc = now
                    });
                setupDb.InventoryTransfers.Add(new LocalInventoryTransfer
                {
                    Id = transferId,
                    TransferNumber = "TR-PURGED-GHOST",
                    TransferDate = DateOnly.FromDateTime(now),
                    FromWarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    ToWarehouseCode =
                        OfficeCodeCatalog.YeonsuMainWarehouse,
                    TransferStatus =
                        InventoryTransferStatusNormalizer.Pending,
                    Memo = "local ghost draft",
                    ReceiveEvidencePath = evidencePath,
                    CreatedByUsername = session.User!.Username,
                    LastSavedByUsername = session.User.Username,
                    Revision = 7,
                    IsDirty = true,
                    CreatedAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now,
                    Lines =
                    [
                        new LocalInventoryTransferLine
                        {
                            Id = Guid.NewGuid(),
                            TransferId = transferId,
                            ItemId = itemId,
                            ItemNameOriginal =
                                "purged transfer stock item",
                            Unit = "EA",
                            Quantity = 2m
                        }
                    ]
                });
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = pullCursor.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)
                });
                await setupDb.SaveChangesAsync();
            }

            var inventoryStateChangedCount = 0;
            await using var observationScope = provider.CreateAsyncScope();
            var observationLocal = observationScope.ServiceProvider
                .GetRequiredService<LocalStateService>();
            observationLocal.InventoryStateChanged += OnInventoryStateChanged;
            var syncSucceeded = false;
            try
            {
                await using var syncScope = provider.CreateAsyncScope();
                var sync = syncScope.ServiceProvider
                    .GetRequiredService<SyncService>();
                if (string.Equals(
                        scenario,
                        "rollback",
                        StringComparison.Ordinal))
                {
                    sync.AfterInventoryTransferPurgePushAppliedAsyncForTesting = _ =>
                        throw new InvalidOperationException(
                            "simulated accepted purge transaction failure");
                }

                var syncTask = sync.TrySyncAsync();
                if (string.Equals(
                        scenario,
                        "owner-aba",
                        StringComparison.Ordinal))
                {
                    await handler.PushReceived.Task.WaitAsync(
                        TimeSpan.FromSeconds(15));
                    try
                    {
                        session.SetBusinessDatabase(
                            TenantScopeCatalog.Itworld);
                        session.SetBusinessDatabase(
                            TenantScopeCatalog.UsenetGroup);
                    }
                    finally
                    {
                        handler.ReleasePushResponse();
                    }
                }
                else if (string.Equals(
                             scenario,
                             "concurrent-edit",
                             StringComparison.Ordinal))
                {
                    await handler.PushReceived.Task.WaitAsync(
                        TimeSpan.FromSeconds(15));
                    await using (var concurrentScope =
                                 provider.CreateAsyncScope())
                    {
                        var concurrentDb = concurrentScope.ServiceProvider
                            .GetRequiredService<LocalDbContext>();
                        var concurrentTransfer = await concurrentDb
                            .InventoryTransfers
                            .IgnoreQueryFilters()
                            .Include(transfer => transfer.Lines)
                            .SingleAsync(transfer => transfer.Id == transferId);
                        concurrentTransfer.Memo =
                            "concurrent latest local draft";
                        concurrentTransfer.Revision = 8;
                        concurrentTransfer.UpdatedAtUtc =
                            now.AddMinutes(1);
                        concurrentTransfer.IsDirty = true;
                        var concurrentLine = Assert.Single(
                            concurrentTransfer.Lines);
                        concurrentLine.Quantity = 3m;
                        concurrentLine.Remark =
                            "concurrent latest line";
                        var concurrentMutationId =
                            $"concurrent:{transferId:N}";
                        concurrentDb.SyncOutboxEntries.Add(
                            new LocalSyncOutboxEntry
                            {
                                MutationId = concurrentMutationId,
                                DeviceId = "concurrent-device",
                                EntityName =
                                    nameof(LocalInventoryTransfer),
                                EntityId = transferId,
                                ExpectedRevision = 7,
                                TenantCode =
                                    TenantScopeCatalog.UsenetGroup,
                                OfficeCode = OfficeCodeCatalog.Usenet,
                                ResponsibleOfficeCode =
                                    OfficeCodeCatalog.Yeonsu,
                                BusinessDatabaseName =
                                    TenantScopeCatalog.GetDatabaseName(
                                        session.SelectedBusinessDatabaseName),
                                SessionId = session.SessionId,
                                UserId = session.User!.UserId,
                                Status = "Prepared",
                                PreparedAtUtc = DateTime.UtcNow.AddMinutes(1)
                            });
                        await concurrentDb.SaveChangesAsync();
                    }

                    handler.ReleasePushResponse();
                }

                syncSucceeded = await syncTask.WaitAsync(
                    TimeSpan.FromSeconds(15));
            }
            finally
            {
                observationLocal.InventoryStateChanged -=
                    OnInventoryStateChanged;
            }

            Assert.Single(handler.PushRequests);
            Assert.Single(
                handler.PushRequests[0].InventoryTransfers,
                transfer =>
                    transfer.Id == transferId && !transfer.IsDeleted);

            await using var verificationDb = new LocalDbContext();
            if (!string.Equals(
                    scenario,
                    "success",
                    StringComparison.Ordinal) &&
                !string.Equals(
                    scenario,
                    "concurrent-edit",
                    StringComparison.Ordinal))
            {
                Assert.False(syncSucceeded);
                Assert.Equal(
                    string.Equals(
                        scenario,
                        "rollback",
                        StringComparison.Ordinal)
                        ? 1
                        : 0,
                    handler.PullCount);
                Assert.Equal(0, inventoryStateChangedCount);

                var preservedTransfer = await verificationDb
                    .InventoryTransfers
                    .IgnoreQueryFilters()
                    .Include(transfer => transfer.Lines)
                    .AsNoTracking()
                    .SingleAsync(transfer => transfer.Id == transferId);
                Assert.False(preservedTransfer.IsDeleted);
                Assert.True(preservedTransfer.IsDirty);
                Assert.Equal(7, preservedTransfer.Revision);
                Assert.Equal("local ghost draft", preservedTransfer.Memo);
                Assert.Equal(evidencePath, preservedTransfer.ReceiveEvidencePath);
                var preservedLine = Assert.Single(preservedTransfer.Lines);
                Assert.Equal(itemId, preservedLine.ItemId);
                Assert.Equal("purged transfer stock item", preservedLine.ItemNameOriginal);
                Assert.Equal("EA", preservedLine.Unit);
                Assert.Equal(2m, preservedLine.Quantity);
                var preservedStocks = await verificationDb
                    .ItemWarehouseStocks
                    .AsNoTracking()
                    .Where(stock => stock.ItemId == itemId)
                    .OrderBy(stock => stock.WarehouseCode)
                    .ToListAsync();
                Assert.Equal(2, preservedStocks.Count);
                Assert.Contains(
                    preservedStocks,
                    stock =>
                        stock.WarehouseCode ==
                            OfficeCodeCatalog.UsenetMainWarehouse &&
                        stock.Quantity == -2m &&
                        stock.Revision == 20);
                Assert.Contains(
                    preservedStocks,
                    stock =>
                        stock.WarehouseCode ==
                            OfficeCodeCatalog.YeonsuMainWarehouse &&
                        stock.Quantity == 2m &&
                        stock.Revision == 21);
                Assert.False(await verificationDb
                    .InventoryTransferTombstoneConflicts
                    .AsNoTracking()
                    .AnyAsync(current =>
                        current.TransferId == transferId));
                Assert.False(await verificationDb
                    .DeferredRecycleBinPurgeRecords
                    .AsNoTracking()
                    .AnyAsync(record => record.Id == receiptId));
                var failedOutbox = await verificationDb
                    .SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(entry =>
                        entry.EntityName ==
                            nameof(LocalInventoryTransfer) &&
                        entry.EntityId == transferId);
                Assert.Equal("Failed", failedOutbox.Status);
                Assert.Equal(0, failedOutbox.AcceptedRevision);
                Assert.Null(failedOutbox.AcknowledgedAtUtc);
                Assert.True(File.Exists(evidencePath));
                Assert.Equal(
                    "purged transfer evidence",
                    await File.ReadAllTextAsync(evidencePath));
                Assert.Equal(
                    pullCursor.ToString(
                        System.Globalization.CultureInfo.InvariantCulture),
                    await verificationDb.Settings
                        .Where(setting =>
                            setting.Key == "LastSyncRevision")
                        .Select(setting => setting.Value)
                        .SingleAsync());
                return;
            }

            Assert.True(syncSucceeded);
            Assert.Equal(1, handler.PullCount);
            Assert.Contains(
                "sinceRev=100",
                handler.LastPullQuery,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, inventoryStateChangedCount);

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

            var conflict = await verificationDb
                .InventoryTransferTombstoneConflicts
                .AsNoTracking()
                .SingleAsync(current => current.TransferId == transferId);
            Assert.Equal(
                InventoryTransferTombstoneConflictPolicy.UnresolvedStatus,
                conflict.Status);
            Assert.Equal(
                string.Equals(
                    scenario,
                    "concurrent-edit",
                    StringComparison.Ordinal)
                    ? 8
                    : 7,
                conflict.LocalRevision);
            Assert.Equal(purgeRevision, conflict.ServerRevision);
            Assert.Contains(
                string.Equals(
                    scenario,
                    "concurrent-edit",
                    StringComparison.Ordinal)
                    ? "concurrent latest local draft"
                    : "local ghost draft",
                conflict.LocalSnapshotJson,
                StringComparison.Ordinal);
            if (string.Equals(
                    scenario,
                    "concurrent-edit",
                    StringComparison.Ordinal))
            {
                Assert.Contains(
                    "concurrent latest line",
                    conflict.LocalSnapshotJson,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "\"Quantity\":3",
                    conflict.LocalSnapshotJson,
                    StringComparison.Ordinal);
            }
            Assert.Contains(
                "\"IsDeleted\":true",
                conflict.ServerTombstoneJson,
                StringComparison.Ordinal);
            Assert.Equal(
                Path.GetFullPath(evidencePath),
                conflict.ArchivedReceiveEvidencePath);

            var outboxRows = await verificationDb.SyncOutboxEntries
                .AsNoTracking()
                .Where(entry =>
                    entry.EntityName == nameof(LocalInventoryTransfer) &&
                    entry.EntityId == transferId)
                .OrderBy(entry => entry.PreparedAtUtc)
                .ToListAsync();
            var outbox = Assert.Single(
                outboxRows,
                entry => entry.Status == "Acknowledged");
            Assert.Equal("Acknowledged", outbox.Status);
            Assert.Equal(purgeRevision, outbox.AcceptedRevision);
            Assert.Contains(
                outbox.MutationId,
                conflict.OutboxMutationIdsJson,
                StringComparison.Ordinal);
            if (string.Equals(
                    scenario,
                    "concurrent-edit",
                    StringComparison.Ordinal))
            {
                var concurrentOutbox = Assert.Single(
                    outboxRows,
                    entry => entry.Status == "Failed");
                Assert.Equal("Failed", concurrentOutbox.Status);
                Assert.Equal(0, concurrentOutbox.AcceptedRevision);
                Assert.Null(concurrentOutbox.AcknowledgedAtUtc);
                Assert.Contains(
                    concurrentOutbox.MutationId,
                    conflict.OutboxMutationIdsJson,
                    StringComparison.Ordinal);
            }
            Assert.False(await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .AnyAsync(record => record.Id == receiptId));
            Assert.True(File.Exists(evidencePath));
            Assert.Equal(
                "purged transfer evidence",
                await File.ReadAllTextAsync(evidencePath));
            Assert.Equal(
                pullCursor.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                await verificationDb.Settings
                    .Where(setting => setting.Key == "LastSyncRevision")
                    .Select(setting => setting.Value)
                    .SingleAsync());

            void OnInventoryStateChanged(object? sender, EventArgs args)
                => inventoryStateChangedCount++;
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("Item", false, false, null)]
    [InlineData("InventoryTransfer", true, false, null)]
    [InlineData("InventoryTransfer", false, true, null)]
    [InlineData("InventoryTransfer", false, false, "Invoice")]
    [InlineData("InventoryTransfer", false, false, "Customer")]
    public async Task RuntimeScopedSync_InventoryTransferStockAtomicityRollback_InvalidResponse_FallsBackFailClosedWithoutQuarantine(
        string noticeEntityName,
        bool useNonEmptyEntityId,
        bool includeExtraNotice,
        string? extraConflictEntityName)
    {
        PrepareAppRoot(
            $"georaeplan-runtime-transfer-stock-atomicity-invalid-response-{noticeEntityName}-{useNonEmptyEntityId}-{includeExtraNotice}-{extraConflictEntityName ?? "none"}");

        try
        {
            var session = CreateAdminSession();
            var transferId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            var handler =
                new InventoryTransferMalformedAtomicityRollbackNoticeHandler(
                    transferId,
                    noticeEntityName,
                    useNonEmptyEntityId,
                    includeExtraNotice,
                    extraConflictEntityName);

            await using var provider = BuildRuntimeProvider(session, handler);
            await using var setupScope = provider.CreateAsyncScope();
            var setupDb =
                setupScope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await setupDb.Database.EnsureDeletedAsync();
            await setupDb.Database.EnsureCreatedAsync();
            setupDb.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "TR-MALFORMED-ROLLBACK-NOTICE",
                FromWarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode =
                    OfficeCodeCatalog.YeonsuMainWarehouse,
                CreatedByUsername = session.User!.Username,
                LastSavedByUsername = session.User.Username,
                Revision = 7,
                IsDirty = true,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = Guid.NewGuid(),
                        TransferId = transferId,
                        ItemNameOriginal = "malformed rollback notice item",
                        Unit = "EA",
                        Quantity = 2m
                    }
                ]
            });
            setupDb.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await setupDb.SaveChangesAsync();
            setupDb.ChangeTracker.Clear();

            await using var runtimeScope = provider.CreateAsyncScope();
            var sync =
                runtimeScope.ServiceProvider.GetRequiredService<SyncService>();

            Assert.False(
                await sync.TrySyncAsync()
                    .WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Single(handler.PushRequests);
            Assert.Single(handler.PushRequests[0].InventoryTransfers);
            Assert.Equal(1, handler.PullCount);

            await using (var afterFirstConflictDb =
                         new LocalDbContext())
            {
                var transfer = await afterFirstConflictDb
                    .InventoryTransfers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(entity => entity.Id == transferId);
                Assert.True(transfer.IsDirty);
                Assert.Equal(7, transfer.Revision);

                var outbox = Assert.Single(
                    await afterFirstConflictDb.SyncOutboxEntries
                        .AsNoTracking()
                        .ToListAsync());
                Assert.Equal(
                    nameof(LocalInventoryTransfer),
                    outbox.EntityName);
                Assert.Equal(transferId, outbox.EntityId);
                Assert.Equal("Failed", outbox.Status);
                Assert.DoesNotContain(
                    "[inventory-transfer-stock-atomicity-rollback]",
                    outbox.ErrorMessage ?? string.Empty,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "Insufficient source stock.",
                    outbox.ErrorMessage ?? string.Empty,
                    StringComparison.Ordinal);

                var lastError = await afterFirstConflictDb.Settings
                    .AsNoTracking()
                    .SingleAsync(setting =>
                        setting.Key == "Sync.LastError");
                Assert.Contains(
                    "Insufficient source stock.",
                    lastError.Value,
                    StringComparison.Ordinal);
            }

            Assert.False(
                await sync.TrySyncAsync()
                    .WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(2, handler.PushRequests.Count);
            Assert.All(
                handler.PushRequests,
                pushedRequest => Assert.Single(
                    pushedRequest.InventoryTransfers,
                    transfer => transfer.Id == transferId));
            Assert.Equal(2, handler.PullCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PushPreparedRequest_DependencyOnlyConflict_RebasesConcurrentUiEditForAcceptedRetry()
    {
        PrepareAppRoot("georaeplan-dependency-only-conflict");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var optionId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var newerUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const string originalName = "dependency option A";
            const string newerName = "dependency option UI edit B";
            db.PriceGradeOptions.Add(new LocalPriceGradeOption
            {
                Id = optionId,
                Name = originalName,
                IsActive = true,
                Revision = 7,
                IsDirty = false,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var preparedOption = LocalMappings.ToDto(
                await db.PriceGradeOptions
                    .AsNoTracking()
                    .SingleAsync(option => option.Id == optionId));
            preparedOption.ExpectedRevision = preparedOption.Revision;
            preparedOption.MutationCreatedAtUtc = preparedOption.UpdatedAtUtc;
            preparedOption.MutationId = $"dependency-only:{optionId:N}";
            var request = new SyncPushRequest
            {
                DeviceId = "dependency-only-device",
                PriceGradeOptions = [preparedOption]
            };
            var handler = new DelayedDependencyConflictThenAckHandler(
                optionId,
                serverRevision: 8,
                acceptedRevision: 9);
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                dispatcher,
                session);
            var api = new ErpApiClient(
                new HttpClient(handler)
                {
                    BaseAddress = new Uri("http://localhost")
                },
                session);
            using var sync = new SyncService(
                db,
                local,
                new RentalStateService(db),
                api,
                session,
                dispatcher,
                new SyncDiagnosticsService(session));

            var pushTask = InvokePushPreparedRequestAsync(
                sync,
                api,
                session,
                request,
                "ITWORLD",
                [(nameof(LocalPriceGradeOption), optionId)]);
            await handler.PushReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(15));
            var trackedOption = await db.PriceGradeOptions
                .SingleAsync(option => option.Id == optionId);
            trackedOption.Name = newerName;
            trackedOption.UpdatedAtUtc = newerUpdatedAtUtc;
            trackedOption.IsDirty = true;
            await db.SaveChangesAsync();
            Assert.Equal(EntityState.Unchanged, db.Entry(trackedOption).State);
            handler.ReleasePush();

            await pushTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(await db.SyncOutboxEntries
                .AsNoTracking()
                .AnyAsync(entry => entry.EntityId == optionId));

            db.ChangeTracker.Clear();
            var rebased = await db.PriceGradeOptions
                .AsNoTracking()
                .SingleAsync(option => option.Id == optionId);
            Assert.Equal(newerName, rebased.Name);
            Assert.True(rebased.IsDirty);
            Assert.Equal(8, rebased.Revision);

            var retryOption = LocalMappings.ToDto(rebased);
            retryOption.ExpectedRevision = rebased.Revision;
            retryOption.MutationCreatedAtUtc = rebased.UpdatedAtUtc;
            retryOption.MutationId = $"dependency-retry:{optionId:N}:8";
            await InvokePushPreparedRequestAsync(
                sync,
                api,
                session,
                new SyncPushRequest
                {
                    DeviceId = "dependency-only-device",
                    PriceGradeOptions = [retryOption]
                },
                "ITWORLD",
                dependencyOnlyKeys: null);

            Assert.Equal(2, handler.PushRequests.Count);
            var retriedOption = Assert.Single(
                handler.PushRequests[1].PriceGradeOptions);
            Assert.Equal(newerName, retriedOption.Name);
            Assert.Equal(8, retriedOption.ExpectedRevision);

            var accepted = await db.PriceGradeOptions
                .AsNoTracking()
                .SingleAsync(option => option.Id == optionId);
            Assert.Equal(newerName, accepted.Name);
            Assert.False(accepted.IsDirty);
            Assert.Equal(9, accepted.Revision);
            Assert.Equal(
                "Acknowledged",
                await db.SyncOutboxEntries
                    .AsNoTracking()
                    .Where(entry => entry.EntityId == optionId)
                    .Select(entry => entry.Status)
                    .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PushPreparedRequest_DependencyOnlyCanonicalIdConflict_MatchesOriginalMutationId()
    {
        PrepareAppRoot("georaeplan-dependency-only-canonical-id-conflict");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var originalCompanyId = Guid.NewGuid();
            var canonicalServerId = Guid.NewGuid();
            var updatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            db.RentalManagementCompanies.Add(new LocalRentalManagementCompany
            {
                Id = originalCompanyId,
                Code = OfficeCodeCatalog.Usenet,
                Name = OfficeCodeCatalog.Usenet,
                IsSystemDefault = true,
                IsActive = true,
                Revision = 7,
                IsDirty = false,
                CreatedAtUtc = updatedAtUtc.AddHours(-1),
                UpdatedAtUtc = updatedAtUtc
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "5"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var preparedCompany = LocalMappings.ToDto(
                await db.RentalManagementCompanies
                    .AsNoTracking()
                    .SingleAsync(company => company.Id == originalCompanyId));
            preparedCompany.ExpectedRevision = preparedCompany.Revision;
            preparedCompany.MutationCreatedAtUtc =
                preparedCompany.UpdatedAtUtc;
            preparedCompany.MutationId =
                $"dependency-only-company:{originalCompanyId:N}";
            var request = new SyncPushRequest
            {
                DeviceId = "dependency-only-company-device",
                RentalManagementCompanies = [preparedCompany]
            };
            var handler =
                new CanonicalizedRentalManagementCompanyDependencyConflictHandler(
                    canonicalServerId,
                    serverRevision: 8);
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                dispatcher,
                session);
            var api = new ErpApiClient(
                new HttpClient(handler)
                {
                    BaseAddress = new Uri("http://localhost")
                },
                session);
            using var sync = new SyncService(
                db,
                local,
                new RentalStateService(db),
                api,
                session,
                dispatcher,
                new SyncDiagnosticsService(session));

            await InvokePushPreparedRequestAsync(
                sync,
                api,
                session,
                request,
                "USENET",
                [(nameof(LocalRentalManagementCompany), originalCompanyId)])
                .WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Single(handler.PushRequests);
            Assert.False(await db.SyncOutboxEntries
                .AsNoTracking()
                .AnyAsync(entry =>
                    entry.EntityId == originalCompanyId ||
                    entry.EntityId == canonicalServerId));
            db.ChangeTracker.Clear();
            var stored = await db.RentalManagementCompanies
                .AsNoTracking()
                .SingleAsync(company => company.Id == originalCompanyId);
            Assert.False(stored.IsDirty);
            Assert.Equal(7, stored.Revision);

            var pullHandler = new AuthoritativePullOnlyHandler(
                new SyncPullResponse
                {
                    CurrentServerRevision = 8,
                    RentalManagementCompanies =
                    [
                        new RentalManagementCompanyDto
                        {
                            Id = canonicalServerId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            Code = OfficeCodeCatalog.Usenet,
                            Name = OfficeCodeCatalog.Usenet,
                            IsSystemDefault = true,
                            IsActive = true,
                            Revision = 8,
                            CreatedAtUtc = updatedAtUtc.AddHours(-1),
                            UpdatedAtUtc = updatedAtUtc.AddMinutes(1)
                        }
                    ]
                });
            using var pullSync = CreateSyncService(db, session, pullHandler);
            Assert.True(await pullSync.TryAuthoritativePullOnlyAsync());
            Assert.Equal(1, pullHandler.PullCount);
            Assert.Equal(0, pullHandler.PushCount);
            Assert.Empty(await db.SyncOutboxEntries.AsNoTracking().ToListAsync());
            Assert.False(await db.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AnyAsync(company => company.Id == originalCompanyId));
            Assert.True(await db.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AnyAsync(company =>
                    company.Id == canonicalServerId &&
                    company.Revision == 8 &&
                    !company.IsDirty));
            Assert.Equal(
                "8",
                await db.Settings.AsNoTracking()
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

    [Theory]
    [InlineData(
        false,
        "Mutation id was already processed with a different entity, expected revision, or payload.")]
    [InlineData(
        true,
        "Expected revision mismatch. client=7, server=8")]
    public async Task PushPreparedRequest_CanonicalDependencyFallback_DoesNotSuppressInvalidOrDirtyOriginal(
        bool originalIsDirty,
        string conflictReason)
    {
        PrepareAppRoot(
            $"georaeplan-canonical-dependency-fallback-negative-{originalIsDirty}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var originalCompanyId = Guid.NewGuid();
            var canonicalServerId = Guid.NewGuid();
            var updatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            db.RentalManagementCompanies.Add(
                new LocalRentalManagementCompany
                {
                    Id = originalCompanyId,
                    Code = OfficeCodeCatalog.Usenet,
                    Name = OfficeCodeCatalog.Usenet,
                    IsSystemDefault = true,
                    IsActive = true,
                    Revision = 7,
                    IsDirty = originalIsDirty,
                    CreatedAtUtc = updatedAtUtc.AddHours(-1),
                    UpdatedAtUtc = updatedAtUtc
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var preparedCompany = LocalMappings.ToDto(
                await db.RentalManagementCompanies
                    .AsNoTracking()
                    .SingleAsync(
                        company => company.Id == originalCompanyId));
            preparedCompany.ExpectedRevision = preparedCompany.Revision;
            preparedCompany.MutationCreatedAtUtc =
                preparedCompany.UpdatedAtUtc;
            preparedCompany.MutationId =
                $"dependency-only-company-negative:{originalCompanyId:N}";
            var request = new SyncPushRequest
            {
                DeviceId = "dependency-only-company-negative-device",
                RentalManagementCompanies = [preparedCompany]
            };
            var handler =
                new CanonicalizedRentalManagementCompanyDependencyConflictHandler(
                    canonicalServerId,
                    serverRevision: 8,
                    conflictReason);
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                dispatcher,
                session);
            var api = new ErpApiClient(
                new HttpClient(handler)
                {
                    BaseAddress = new Uri("http://localhost")
                },
                session);
            using var sync = new SyncService(
                db,
                local,
                new RentalStateService(db),
                api,
                session,
                dispatcher,
                new SyncDiagnosticsService(session));

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => InvokePushPreparedRequestAsync(
                        sync,
                        api,
                        session,
                        request,
                        "USENET",
                        [(
                            nameof(LocalRentalManagementCompany),
                            originalCompanyId)])
                    .WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Contains(
                conflictReason,
                error.Message,
                StringComparison.Ordinal);
            Assert.Single(handler.PushRequests);
            db.ChangeTracker.Clear();
            Assert.Equal(
                originalIsDirty,
                await db.RentalManagementCompanies
                    .AsNoTracking()
                    .Where(company => company.Id == originalCompanyId)
                    .Select(company => company.IsDirty)
                    .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbContext_DirtyRootSave_ReassertsMarkerAfterSiblingSyncClean()
    {
        PrepareAppRoot("georaeplan-dirty-root-save-marker");

        try
        {
            var transferId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            await using var uiDb = new LocalDbContext();
            await uiDb.Database.EnsureDeletedAsync();
            await uiDb.Database.EnsureCreatedAsync();
            uiDb.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "TR-DIRTY-ROOT",
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Memo = "prepared payload A",
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now,
                Revision = 21,
                IsDirty = true
            });
            await uiDb.SaveChangesAsync();
            uiDb.ChangeTracker.Clear();

            var trackedTransfer = await uiDb.InventoryTransfers
                .IgnoreQueryFilters()
                .SingleAsync(transfer => transfer.Id == transferId);
            trackedTransfer.Memo = "newer UI payload B";
            trackedTransfer.UpdatedAtUtc = now.AddMinutes(1);
            uiDb.ChangeTracker.DetectChanges();
            Assert.Equal(
                EntityState.Modified,
                uiDb.Entry(trackedTransfer).State);
            Assert.False(uiDb.Entry(trackedTransfer)
                .Property(transfer => transfer.IsDirty)
                .IsModified);

            await using (var syncDb = new LocalDbContext())
            {
                Assert.Equal(
                    1,
                    await syncDb.InventoryTransfers
                        .IgnoreQueryFilters()
                        .Where(transfer => transfer.Id == transferId)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(transfer => transfer.IsDirty, false)
                            .SetProperty(transfer => transfer.Revision, 22)));
            }

            await uiDb.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.InventoryTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(transfer => transfer.Id == transferId);
            Assert.Equal("newer UI payload B", saved.Memo);
            Assert.True(saved.IsDirty);
            Assert.Equal(22, saved.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbContext_DirtyLineSave_ReassertsRootAfterSiblingSyncClean()
    {
        PrepareAppRoot("georaeplan-dirty-transfer-line-marker");

        try
        {
            var transferId = Guid.NewGuid();
            var lineId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            await using var uiDb = new LocalDbContext();
            await uiDb.Database.EnsureDeletedAsync();
            await uiDb.Database.EnsureCreatedAsync();
            uiDb.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "TR-DIRTY-LINE",
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now,
                Revision = 41,
                IsDirty = true,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = lineId,
                        TransferId = transferId,
                        ItemNameOriginal = "dirty graph item",
                        Unit = "EA",
                        Quantity = 1m
                    }
                ]
            });
            await uiDb.SaveChangesAsync();
            uiDb.ChangeTracker.Clear();

            var trackedTransfer = await uiDb.InventoryTransfers
                .IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .SingleAsync(transfer => transfer.Id == transferId);
            var trackedLine = Assert.Single(trackedTransfer.Lines);
            trackedLine.ReceiptRemark = "newer UI line payload";
            uiDb.ChangeTracker.DetectChanges();
            Assert.Equal(
                EntityState.Unchanged,
                uiDb.Entry(trackedTransfer).State);
            Assert.True(trackedTransfer.IsDirty);
            Assert.Equal(
                EntityState.Modified,
                uiDb.Entry(trackedLine).State);

            await using (var syncDb = new LocalDbContext())
            {
                Assert.Equal(
                    1,
                    await syncDb.InventoryTransfers
                        .IgnoreQueryFilters()
                        .Where(transfer => transfer.Id == transferId)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(transfer => transfer.IsDirty, false)
                            .SetProperty(transfer => transfer.Revision, 42)));
            }

            await uiDb.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.InventoryTransfers
                .IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .AsNoTracking()
                .SingleAsync(transfer => transfer.Id == transferId);
            Assert.True(saved.IsDirty);
            Assert.Equal(42, saved.Revision);
            Assert.Equal(
                "newer UI line payload",
                Assert.Single(saved.Lines).ReceiptRemark);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalDbContext_CleanLineSave_DoesNotDirtyRootOrNextRequest()
    {
        PrepareAppRoot("georaeplan-clean-transfer-line");

        try
        {
            var transferId = Guid.NewGuid();
            var lineId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "TR-CLEAN-PULL",
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now,
                Revision = 31,
                IsDirty = false,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = lineId,
                        TransferId = transferId,
                        ItemNameOriginal = "clean pulled item",
                        Unit = "EA",
                        Quantity = 1m
                    }
                ]
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var trackedTransfer = await db.InventoryTransfers
                .IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .SingleAsync(transfer => transfer.Id == transferId);
            Assert.False(trackedTransfer.IsDirty);
            Assert.Equal(
                EntityState.Unchanged,
                db.Entry(trackedTransfer).State);
            Assert.Single(trackedTransfer.Lines).ReceiptRemark =
                "server pull line update";
            await db.SaveChangesAsync();

            db.ChangeTracker.Clear();
            Assert.False(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(transfer => transfer.Id == transferId)
                .Select(transfer => transfer.IsDirty)
                .SingleAsync());

            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            Assert.Empty(
                await local.GetDirtyInventoryTransfersForSyncAsync(session));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void InventoryTransferPreparedPayloadHash_IsStableAcrossMaterializationOrder()
    {
        var transferId =
            Guid.Parse("e9257a35-3f12-4fb0-bbcc-0a9659c56fa1");
        var lineM =
            Guid.Parse("2b43032d-ceba-4672-939d-54e11da919a7");
        var lineC =
            Guid.Parse("c0efdeca-26bf-45bf-a75c-a76b9bb335cd");
        var lineY =
            Guid.Parse("eb88f2ca-0f3b-4afb-a9db-3d65ea278e81");
        var timestamp =
            new DateTime(2026, 7, 21, 7, 59, 11, DateTimeKind.Utc);
        var linesById = new Dictionary<Guid, LocalInventoryTransferLine>
        {
            [lineM] = new()
            {
                Id = lineM,
                TransferId = transferId,
                ItemId = Guid.Parse(
                    "5b80395c-f388-5059-bd48-91b5d608e0d6"),
                ItemNameOriginal = "PCDU[M]",
                Unit = "EA",
                Quantity = 1m,
                ReceivedQuantity = 1m,
                QuantityDifference = 0m
            },
            [lineC] = new()
            {
                Id = lineC,
                TransferId = transferId,
                ItemId = Guid.Parse(
                    "fc150609-1558-5e37-9621-f1b0e59db6d1"),
                ItemNameOriginal = "PCDU[C]",
                Unit = "EA",
                Quantity = 1m,
                ReceivedQuantity = 1m,
                QuantityDifference = 0m
            },
            [lineY] = new()
            {
                Id = lineY,
                TransferId = transferId,
                ItemId = Guid.Parse(
                    "5515e7a4-15b1-5604-a1b6-582187e45fe8"),
                ItemNameOriginal = "PCDU[Y]",
                Unit = "EA",
                Quantity = 1m,
                ReceivedQuantity = 1m,
                QuantityDifference = 0m
            }
        };

        LocalInventoryTransfer CreateTransfer(params Guid[] lineOrder)
            => new()
            {
                Id = transferId,
                TransferNumber = "TR202607-0001",
                TransferDate = new DateOnly(2026, 7, 21),
                FromWarehouseCode = "USENET_MAIN",
                ToWarehouseCode = "YEONSU_MAIN",
                CreatedByUsername = "usenet",
                LastSavedByUsername = "usenet",
                LastSavedAtUtc = timestamp,
                TransferStatus = "수령대기",
                RequestedByUsername = "usenet",
                RequestedAtUtc = timestamp,
                LastStatusChangedByUsername = "usenet",
                LastStatusChangedAtUtc = timestamp,
                CreatedAtUtc = timestamp,
                UpdatedAtUtc = timestamp,
                Revision = 1784889970925,
                IsDirty = true,
                Lines = lineOrder
                    .Select(lineId => linesById[lineId])
                    .ToList()
            };

        var indexMaterialized = LocalMappings.ToDto(
            CreateTransfer(lineY, lineM, lineC));
        var idMaterialized = LocalMappings.ToDto(
            CreateTransfer(lineM, lineC, lineY));

        var expectedOrder = new[] { lineM, lineC, lineY }
            .OrderBy(id => id)
            .ToArray();
        Assert.Equal(
            expectedOrder,
            indexMaterialized.Lines.Select(line => line.Id));
        Assert.Equal(
            InvokeComputePreparedMutationPayloadHash(
                "InventoryTransfer",
                idMaterialized),
            InvokeComputePreparedMutationPayloadHash(
                "InventoryTransfer",
                indexMaterialized));
    }

    [Fact]
    public async Task RuntimeScopedSync_AllowsSharedUiContextSaveDuringPush()
    {
        PrepareAppRoot("georaeplan-runtime-scoped-sync-ui-save");

        try
        {
            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            const string originalName = "runtime scoped customer";
            const string modifiedName = "runtime scoped customer saved during push";
            var handler = new DelayedPushAckThenEmptyPullHandler(
                unitId,
                entityName: "Unit",
                acceptedRevision: 12,
                acceptedUpdatedAtUtc: now.AddMinutes(1));

            var services = new ServiceCollection();
            services.AddDbContext<LocalDbContext>();
            services.AddSingleton(session);
            services.AddSingleton(new OfficeAccessService());
            services.AddSingleton(new SyncRequestDispatcher());
            services.AddScoped<SyncDiagnosticsService>();
            services.AddScoped<LocalStateService>();
            services.AddScoped<RentalStateService>();
            services.AddScoped(_ => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost")
            });
            services.AddScoped<ErpApiClient>();
            services.AddScoped<SyncService>();

            await using var provider = services.BuildServiceProvider();
            await using var runtimeScope = provider.CreateAsyncScope();
            var db = runtimeScope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = "runtime scoped prepared unit",
                IsActive = true,
                Revision = 11,
                IsDirty = true,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = originalName,
                NameMatchKey = originalName,
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var sync = runtimeScope.ServiceProvider.GetRequiredService<SyncService>();
            var syncTask = sync.TrySyncAsync();
            try
            {
                var pushedRequest = await handler.PushReceived.Task.WaitAsync(
                    TimeSpan.FromSeconds(15));
                Assert.Single(pushedRequest.Units, unit => unit.Id == unitId);

                var trackedCustomer = await db.Customers.IgnoreQueryFilters()
                    .SingleAsync(customer => customer.Id == customerId);
                trackedCustomer.NameOriginal = modifiedName;
                trackedCustomer.IsDirty = true;
                await db.SaveChangesAsync();
                Assert.Equal(EntityState.Unchanged, db.Entry(trackedCustomer).State);

                await using var verificationDb = new LocalDbContext();
                var savedDuringPush = await verificationDb.Customers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(customer => customer.Id == customerId);
                Assert.Equal(modifiedName, savedDuringPush.NameOriginal);
                Assert.True(savedDuringPush.IsDirty);
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.True(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordPreparedMutationsAsync_IsolatedSave_DoesNotSweepEditCreatedDuringPreparation(
        bool throwDuringPreparation)
    {
        PrepareAppRoot("georaeplan-isolated-outbox-prepare-save");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            const string originalName = "customer before outbox preparation";
            const string modifiedName = "customer edited during outbox preparation";
            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = "prepared unit for isolated outbox save",
                IsActive = true,
                Revision = 20,
                IsDirty = true,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = originalName,
                NameMatchKey = originalName,
                Revision = 8,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPushAckThenEmptyPullHandler(
                unitId,
                entityName: "Unit",
                acceptedRevision: 21,
                acceptedUpdatedAtUtc: now.AddMinutes(1));
            using var sync = CreateSyncService(db, session, handler);
            var preparationReached = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePreparation = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            sync.BeforePreparedOutboxSaveAsyncForTesting = async ct =>
            {
                preparationReached.TrySetResult(true);
                await releasePreparation.Task.WaitAsync(ct);
                if (throwDuringPreparation)
                {
                    throw new InvalidOperationException(
                        "simulated prepared outbox save boundary failure");
                }
            };

            var syncTask = sync.TrySyncAsync();
            await preparationReached.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var trackedCustomer = await db.Customers.IgnoreQueryFilters()
                .SingleAsync(customer => customer.Id == customerId);
            trackedCustomer.NameOriginal = modifiedName;
            db.ChangeTracker.DetectChanges();
            Assert.Equal(EntityState.Modified, db.Entry(trackedCustomer).State);
            releasePreparation.TrySetResult(true);

            if (!throwDuringPreparation)
            {
                try
                {
                    await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
                }
                finally
                {
                    handler.ReleasePush();
                }
            }

            Assert.Equal(
                !throwDuringPreparation,
                await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));
            await using var verificationDb = new LocalDbContext();
            var persistedBeforeExplicitSave = await verificationDb.Customers
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(customer => customer.Id == customerId);
            Assert.Equal(originalName, persistedBeforeExplicitSave.NameOriginal);
            Assert.False(persistedBeforeExplicitSave.IsDirty);
            Assert.Equal(modifiedName, trackedCustomer.NameOriginal);
            Assert.True(trackedCustomer.IsDirty);
            Assert.Equal(EntityState.Modified, db.Entry(trackedCustomer).State);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaptureTrackedChangesBeforePreparedMutationBoundary_PreservesNewerEdit(
        bool includeOlderPreparedSnapshot)
    {
        PrepareAppRoot("georaeplan-prepared-mutation-boundary");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var newerUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const string originalName = "unit before prepared mutation boundary";
            const string newerName = "unit edited after prepared mutation snapshot";
            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = originalName,
                IsActive = true,
                Revision = 31,
                IsDirty = true,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var trackedUnit = await db.Units.IgnoreQueryFilters()
                .SingleAsync(unit => unit.Id == unitId);
            var preparedRequest = new SyncPushRequest();
            if (includeOlderPreparedSnapshot)
            {
                var preparedUnit = LocalMappings.ToDto(trackedUnit);
                preparedUnit.ExpectedRevision = trackedUnit.Revision;
                preparedUnit.MutationCreatedAtUtc = trackedUnit.UpdatedAtUtc;
                preparedUnit.MutationId = $"prepared-boundary:{unitId:N}";
                preparedRequest.Units.Add(preparedUnit);
            }

            trackedUnit.Name = newerName;
            trackedUnit.UpdatedAtUtc = newerUpdatedAtUtc;
            trackedUnit.IsDirty = true;
            db.ChangeTracker.DetectChanges();
            Assert.Equal(EntityState.Modified, db.Entry(trackedUnit).State);

            using var sync = CreateSyncService(db, session);
            InvokeCaptureTrackedChangesBeforePreparedMutationBoundary(
                sync,
                preparedRequest);

            Assert.Equal(EntityState.Detached, db.Entry(trackedUnit).State);
            InvokeRestoreTrackedMutationsPreservedDuringSync(sync);

            Assert.Equal(newerName, trackedUnit.Name);
            Assert.Equal(newerUpdatedAtUtc, trackedUnit.UpdatedAtUtc);
            Assert.True(trackedUnit.IsDirty);
            Assert.Equal(31, trackedUnit.Revision);
            Assert.Equal(EntityState.Modified, db.Entry(trackedUnit).State);

            await db.SaveChangesAsync();
            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(newerName, saved.Name);
            Assert.Equal(newerUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.True(saved.IsDirty);
            Assert.Equal(31, saved.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("5", true)]
    [InlineData("0", false)]
    public async Task RuntimeScopedSync_EditDuringPull_DefersApplyAndPreservesDraft(
        string lastSyncRevision,
        bool expectedSyncResult)
    {
        PrepareAppRoot("georaeplan-runtime-scoped-pull-owner-edit");

        try
        {
            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            const string originalName = "unit before runtime scoped pull";
            const string draftName = "unit draft created during pull";
            const string serverName = "server unit returned during pull";
            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 10,
                    Units =
                    [
                        new UnitDto
                        {
                            Id = unitId,
                            Name = serverName,
                            IsActive = true,
                            Revision = 10,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now.AddMinutes(1)
                        }
                    ]
                });

            var services = new ServiceCollection();
            services.AddDbContext<LocalDbContext>();
            services.AddSingleton(session);
            services.AddSingleton(new OfficeAccessService());
            services.AddSingleton(new SyncRequestDispatcher());
            services.AddScoped<SyncDiagnosticsService>();
            services.AddScoped<LocalStateService>();
            services.AddScoped<RentalStateService>();
            services.AddScoped(_ => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost")
            });
            services.AddScoped<ErpApiClient>();
            services.AddScoped<SyncService>();

            await using var provider = services.BuildServiceProvider();
            await using var runtimeScope = provider.CreateAsyncScope();
            var db = runtimeScope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = originalName,
                IsActive = true,
                Revision = 5,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = lastSyncRevision
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var trackedUnit = await db.Units.IgnoreQueryFilters()
                .SingleAsync(unit => unit.Id == unitId);
            var sync = runtimeScope.ServiceProvider.GetRequiredService<SyncService>();
            var syncTask = sync.TrySyncAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            trackedUnit.Name = draftName;
            db.ChangeTracker.DetectChanges();
            Assert.Equal(EntityState.Modified, db.Entry(trackedUnit).State);
            handler.ReleasePull();

            Assert.Equal(
                expectedSyncResult,
                await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(draftName, trackedUnit.Name);
            Assert.Equal(EntityState.Modified, db.Entry(trackedUnit).State);

            await using var verificationDb = new LocalDbContext();
            var persisted = await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(originalName, persisted.Name);
            Assert.NotEqual(serverName, persisted.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RuntimeScopedSync_DisposedRoot_DoesNotCreateChildOperation()
    {
        PrepareAppRoot("georaeplan-runtime-scoped-disposed-root");

        try
        {
            var session = CreateAdminSession();
            var handler = new DelayedPullHandler();
            var services = new ServiceCollection();
            services.AddDbContext<LocalDbContext>();
            services.AddSingleton(session);
            services.AddSingleton(new OfficeAccessService());
            services.AddSingleton(new SyncRequestDispatcher());
            services.AddScoped<SyncDiagnosticsService>();
            services.AddScoped<LocalStateService>();
            services.AddScoped<RentalStateService>();
            services.AddScoped(_ => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost")
            });
            services.AddScoped<ErpApiClient>();
            services.AddScoped<SyncService>();

            await using var provider = services.BuildServiceProvider();
            await using var runtimeScope = provider.CreateAsyncScope();
            var db = runtimeScope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var sync = runtimeScope.ServiceProvider.GetRequiredService<SyncService>();
            sync.Dispose();

            Assert.False(await sync.RefreshSharedMirrorFromServerAsync());
            Assert.False(await sync.RefreshCurrentBusinessScopeFromServerAsync());
            Assert.False(await sync.EnsureAdministrativeBusinessCachesAsync());
            Assert.Equal(0, handler.PullCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RuntimeScopedFullMirror_SavedDirtyAtResetBoundary_AbortsBeforeReset()
    {
        PrepareAppRoot("georaeplan-runtime-scoped-full-mirror-reset-boundary");

        try
        {
            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            const string originalName = "unit before full mirror reset";
            const string savedName = "unit saved at full mirror reset boundary";
            const string serverName = "server unit for full mirror reset";
            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 14,
                    Units =
                    [
                        new UnitDto
                        {
                            Id = unitId,
                            Name = serverName,
                            IsActive = true,
                            Revision = 14,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now.AddMinutes(1)
                        }
                    ]
                });

            var services = new ServiceCollection();
            services.AddDbContext<LocalDbContext>();
            services.AddSingleton(session);
            services.AddSingleton(new OfficeAccessService());
            services.AddSingleton(new SyncRequestDispatcher());
            services.AddScoped<SyncDiagnosticsService>();
            services.AddScoped<LocalStateService>();
            services.AddScoped<RentalStateService>();
            services.AddScoped(_ => new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost")
            });
            services.AddScoped<ErpApiClient>();
            services.AddScoped<SyncService>();

            await using var provider = services.BuildServiceProvider();
            await using var runtimeScope = provider.CreateAsyncScope();
            var db = runtimeScope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = originalName,
                IsActive = true,
                Revision = 7,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "0"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var trackedUnit = await db.Units.IgnoreQueryFilters()
                .SingleAsync(unit => unit.Id == unitId);
            var resetBoundaryReached = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseResetBoundary = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var sync = runtimeScope.ServiceProvider.GetRequiredService<SyncService>();
            sync.BeforeSharedMirrorResetAsyncForTesting = async ct =>
            {
                resetBoundaryReached.TrySetResult(true);
                await releaseResetBoundary.Task.WaitAsync(ct);
            };

            var syncTask = sync.TrySyncAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.ReleasePull();
            await resetBoundaryReached.Task.WaitAsync(TimeSpan.FromSeconds(15));

            try
            {
                trackedUnit.Name = savedName;
                trackedUnit.IsDirty = true;
                await db.SaveChangesAsync();
            }
            finally
            {
                releaseResetBoundary.TrySetResult(true);
            }

            Assert.False(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));
            await using var verificationDb = new LocalDbContext();
            var persisted = await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(savedName, persisted.Name);
            Assert.True(persisted.IsDirty);
            Assert.NotEqual(serverName, persisted.Name);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task HasPendingSyncChangesAsync_TreatsNonAcknowledgedOutboxAsPending()
    {
        PrepareAppRoot("georaeplan-outbox-pending-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            db.SyncOutboxEntries.Add(CreateOutboxEntry("Sent"));
            await db.SaveChangesAsync();

            Assert.True(await local.HasPendingSyncChangesAsync());

            var summary = await local.GetPendingSyncSummaryAsync();
            Assert.Contains(summary.Buckets, bucket => bucket.EntityDisplayName == "동기화 전송 확인");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MarkOutboxAcknowledgedForCleanEntities_RequiresNewerServerRevisionAndStableOwnerScope()
    {
        PrepareAppRoot("georaeplan-outbox-reconcile-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "outbox 서버 확인 거래처",
                NameMatchKey = "outbox 서버 확인 거래처",
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now,
                Revision = 12,
                IsDirty = false
            });
            const string currentDeviceId = "test-device";
            var currentBusinessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                session.SelectedBusinessDatabaseName);
            var matchingOutboxId = Guid.NewGuid();
            var differentDatabaseOutboxId = Guid.NewGuid();
            var differentTenantOutboxId = Guid.NewGuid();
            var differentOfficeOutboxId = Guid.NewGuid();
            var differentResponsibleOfficeOutboxId = Guid.NewGuid();
            var differentDeviceOutboxId = Guid.NewGuid();
            var differentSessionOutboxId = Guid.NewGuid();
            var differentUserOutboxId = Guid.NewGuid();
            var legacyMissingDatabaseOutboxId = Guid.NewGuid();
            var legacyMissingSessionOutboxId = Guid.NewGuid();
            var legacyMissingUserOutboxId = Guid.NewGuid();
            var legacyMissingMutationOutboxId = Guid.NewGuid();
            var equalRevisionOutboxId = Guid.NewGuid();
            var greaterExpectedRevisionOutboxId = Guid.NewGuid();

            LocalSyncOutboxEntry CreateReconciliationCandidate(
                Guid id,
                long expectedRevision = 11,
                string? businessDatabaseName = null,
                string? tenantCode = null,
                string? officeCode = null,
                string? responsibleOfficeCode = null,
                string? deviceId = null,
                Guid? sessionId = null,
                Guid? userId = null)
                => new()
                {
                    Id = id,
                    MutationId = $"{currentDeviceId}:{nameof(LocalCustomer)}:{customerId:N}:{id:N}",
                    DeviceId = deviceId ?? currentDeviceId,
                    EntityName = nameof(LocalCustomer),
                    EntityId = customerId,
                    ExpectedRevision = expectedRevision,
                    TenantCode = tenantCode ?? TenantScopeCatalog.UsenetGroup,
                    OfficeCode = officeCode ?? OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = responsibleOfficeCode ?? OfficeCodeCatalog.Usenet,
                    BusinessDatabaseName = businessDatabaseName ?? currentBusinessDatabaseName,
                    SessionId = sessionId ?? session.SessionId,
                    UserId = userId ?? session.User!.UserId,
                    Status = "Sent",
                    PreparedAtUtc = now.AddMinutes(-5),
                    SentAtUtc = now.AddMinutes(-4)
                };

            db.SyncOutboxEntries.AddRange(
                CreateReconciliationCandidate(matchingOutboxId),
                CreateReconciliationCandidate(
                    differentDatabaseOutboxId,
                    businessDatabaseName: "ITWORLD"),
                CreateReconciliationCandidate(
                    differentTenantOutboxId,
                    tenantCode: TenantScopeCatalog.Itworld),
                CreateReconciliationCandidate(
                    differentOfficeOutboxId,
                    officeCode: OfficeCodeCatalog.Itworld),
                CreateReconciliationCandidate(
                    differentResponsibleOfficeOutboxId,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld),
                CreateReconciliationCandidate(
                    differentDeviceOutboxId,
                    deviceId: "other-device"),
                CreateReconciliationCandidate(
                    differentSessionOutboxId,
                    sessionId: Guid.NewGuid()),
                CreateReconciliationCandidate(
                    differentUserOutboxId,
                    userId: Guid.NewGuid()),
                CreateReconciliationCandidate(
                    equalRevisionOutboxId,
                    expectedRevision: 12),
                CreateReconciliationCandidate(
                    greaterExpectedRevisionOutboxId,
                    expectedRevision: 13));
            var legacyMissingDatabase = CreateReconciliationCandidate(
                legacyMissingDatabaseOutboxId);
            legacyMissingDatabase.BusinessDatabaseName = string.Empty;
            var legacyMissingSession = CreateReconciliationCandidate(
                legacyMissingSessionOutboxId);
            legacyMissingSession.SessionId = Guid.Empty;
            var legacyMissingUser = CreateReconciliationCandidate(
                legacyMissingUserOutboxId);
            legacyMissingUser.UserId = Guid.Empty;
            var legacyMissingMutation = CreateReconciliationCandidate(
                legacyMissingMutationOutboxId);
            legacyMissingMutation.MutationId = string.Empty;
            db.SyncOutboxEntries.AddRange(
                legacyMissingDatabase,
                legacyMissingSession,
                legacyMissingUser,
                legacyMissingMutation);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var changedWithoutServerEvidence = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                sync,
                [],
                session,
                currentDeviceId,
                currentBusinessDatabaseName);

            Assert.Equal(0, changedWithoutServerEvidence);
            Assert.All(
                await db.SyncOutboxEntries.AsNoTracking().ToListAsync(),
                row => Assert.Equal("Sent", row.Status));

            var mismatchDto = LocalMappings.ToDto(await db.Customers.AsNoTracking().SingleAsync(customer => customer.Id == customerId));
            mismatchDto.NameOriginal = "서버의 다른 거래처명";
            mismatchDto.NameMatchKey = "서버의 다른 거래처명";
            var changedWithMismatch = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                sync,
                [mismatchDto],
                session,
                currentDeviceId,
                currentBusinessDatabaseName);

            Assert.Equal(0, changedWithMismatch);
            Assert.All(
                await db.SyncOutboxEntries.AsNoTracking().ToListAsync(),
                row => Assert.Equal("Sent", row.Status));

            var deletedMismatchDto = LocalMappings.ToDto(
                await db.Customers.AsNoTracking().SingleAsync(customer => customer.Id == customerId));
            deletedMismatchDto.IsDeleted = true;
            var changedWithDeletedMismatch = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                sync,
                [deletedMismatchDto],
                session,
                currentDeviceId,
                currentBusinessDatabaseName);

            Assert.Equal(0, changedWithDeletedMismatch);
            Assert.All(
                await db.SyncOutboxEntries.AsNoTracking().ToListAsync(),
                row => Assert.Equal("Sent", row.Status));

            var matchingDto = LocalMappings.ToDto(await db.Customers.AsNoTracking().SingleAsync(customer => customer.Id == customerId));
            var changedWithMatchingServerSnapshot = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                sync,
                [matchingDto],
                session,
                currentDeviceId,
                currentBusinessDatabaseName);

            Assert.Equal(2, changedWithMatchingServerSnapshot);
            Assert.Equal(
                "Acknowledged",
                await ReadOutboxStatusAsync(db, matchingOutboxId));
            Assert.Equal(
                "Acknowledged",
                await ReadOutboxStatusAsync(db, differentSessionOutboxId));
            var acknowledgedOutbox = await db.SyncOutboxEntries
                .AsNoTracking()
                .SingleAsync(row => row.Id == matchingOutboxId);
            Assert.Equal(matchingDto.Revision, acknowledgedOutbox.AcceptedRevision);
            Assert.Equal(matchingDto.UpdatedAtUtc, acknowledgedOutbox.AcceptedUpdatedAtUtc);
            var protectedIds = new[]
            {
                differentDatabaseOutboxId,
                differentTenantOutboxId,
                differentOfficeOutboxId,
                differentResponsibleOfficeOutboxId,
                differentDeviceOutboxId,
                differentUserOutboxId,
                legacyMissingDatabaseOutboxId,
                legacyMissingSessionOutboxId,
                legacyMissingUserOutboxId,
                legacyMissingMutationOutboxId,
                equalRevisionOutboxId,
                greaterExpectedRevisionOutboxId
            };
            var protectedRows = await db.SyncOutboxEntries
                .AsNoTracking()
                .Where(row => protectedIds.Contains(row.Id))
                .ToListAsync();
            Assert.Equal(protectedIds.Length, protectedRows.Count);
            Assert.All(protectedRows, row => Assert.Equal("Sent", row.Status));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MarkOutboxAcknowledgedForCleanEntities_RequiresVerifiedEntityOrParentScopeEvidence()
    {
        PrepareAppRoot("georaeplan-outbox-reconcile-parent-scope-evidence");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            await db.Database.OpenConnectionAsync();
            await using (var foreignKeys = db.Database.GetDbConnection().CreateCommand())
            {
                foreignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
                await foreignKeys.ExecuteNonQueryAsync();
            }

            var session = CreateAdminSession();
            const string deviceId = "test-device";
            var businessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                session.SelectedBusinessDatabaseName);
            var now = DateTime.UtcNow;
            var missingCustomerId = Guid.NewGuid();
            var missingItemId = Guid.NewGuid();
            var missingTransactionId = Guid.NewGuid();
            var missingInvoiceId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var priceGradeId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var paymentId = Guid.NewGuid();
            var invalidCustomerId = Guid.NewGuid();

            var contract = new LocalCustomerContract
            {
                Id = contractId,
                CustomerId = missingCustomerId,
                ContractType = "scope evidence contract",
                FileName = "scope-evidence.pdf",
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            };
            var priceGrade = new LocalItemPriceGrade
            {
                Id = priceGradeId,
                ItemId = missingItemId,
                PriceGradeOptionId = Guid.NewGuid(),
                PriceGradeName = "scope evidence grade",
                UnitPrice = 10_000m,
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            };
            var attachment = new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = missingTransactionId,
                AttachmentType = "기타",
                FileName = "scope-evidence.pdf",
                StoredFileName = "scope-evidence.pdf",
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            };
            var payment = new LocalPayment
            {
                Id = paymentId,
                InvoiceId = missingInvoiceId,
                PaymentDate = new DateOnly(2026, 8, 8),
                Amount = 10_000m,
                Note = "scope evidence payment",
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            };
            var invalidCustomer = new LocalCustomer
            {
                Id = invalidCustomerId,
                TenantCode = string.Empty,
                OfficeCode = string.Empty,
                ResponsibleOfficeCode = "INVALID-OFFICE",
                NameOriginal = "invalid scope customer",
                NameMatchKey = "INVALIDSCOPECUSTOMER",
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            };
            db.CustomerContracts.Add(contract);
            db.ItemPriceGrades.Add(priceGrade);
            db.TransactionAttachments.Add(attachment);
            db.Payments.Add(payment);
            db.Customers.Add(invalidCustomer);

            LocalSyncOutboxEntry CreateCandidate(string entityName, Guid entityId)
                => new()
                {
                    Id = Guid.NewGuid(),
                    MutationId = $"{deviceId}:{entityName}:{entityId:N}:3",
                    DeviceId = deviceId,
                    EntityName = entityName,
                    EntityId = entityId,
                    ExpectedRevision = 3,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BusinessDatabaseName = businessDatabaseName,
                    SessionId = session.SessionId,
                    UserId = session.User!.UserId,
                    Status = "Sent",
                    PreparedAtUtc = now.AddMinutes(-5),
                    SentAtUtc = now.AddMinutes(-4)
                };

            var contractOutbox = CreateCandidate(nameof(LocalCustomerContract), contractId);
            var priceGradeOutbox = CreateCandidate(nameof(LocalItemPriceGrade), priceGradeId);
            var attachmentOutbox = CreateCandidate(nameof(LocalTransactionAttachment), attachmentId);
            var paymentOutbox = CreateCandidate(nameof(LocalPayment), paymentId);
            var invalidCustomerOutbox = CreateCandidate(nameof(LocalCustomer), invalidCustomerId);
            db.SyncOutboxEntries.AddRange(
                contractOutbox,
                priceGradeOutbox,
                attachmentOutbox,
                paymentOutbox,
                invalidCustomerOutbox);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var contractDto = LocalMappings.ToDto(contract);
            var priceGradeDto = LocalMappings.ToDto(priceGrade);
            var attachmentDto = LocalMappings.ToDto(attachment);
            var paymentDto = LocalMappings.ToDto(payment);
            var invalidCustomerDto = LocalMappings.ToDto(invalidCustomer);
            invalidCustomerDto.TenantCode = string.Empty;
            invalidCustomerDto.OfficeCode = string.Empty;
            invalidCustomerDto.ResponsibleOfficeCode = "INVALID-OFFICE";
            using var sync = CreateSyncService(db, session);

            Assert.Equal(
                0,
                await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomerContract, CustomerContractDto>(
                    sync, [contractDto], session, deviceId, businessDatabaseName));
            Assert.Equal(
                0,
                await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalItemPriceGrade, ItemPriceGradeDto>(
                    sync, [priceGradeDto], session, deviceId, businessDatabaseName));
            Assert.Equal(
                0,
                await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalTransactionAttachment, TransactionAttachmentDto>(
                    sync, [attachmentDto], session, deviceId, businessDatabaseName));
            Assert.Equal(
                0,
                await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalPayment, PaymentDto>(
                    sync, [paymentDto], session, deviceId, businessDatabaseName));
            Assert.Equal(
                0,
                await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                    sync, [invalidCustomerDto], session, deviceId, businessDatabaseName));

            Assert.All(
                await db.SyncOutboxEntries.AsNoTracking().ToListAsync(),
                row => Assert.Equal("Sent", row.Status));

            db.Customers.Add(new LocalCustomer
            {
                Id = missingCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "verified parent customer",
                NameMatchKey = "VERIFIEDPARENTCUSTOMER",
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddHours(-1)
            });
            db.Items.Add(new LocalItem
            {
                Id = missingItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "verified parent item",
                NameMatchKey = "VERIFIEDPARENTITEM",
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddHours(-1)
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = missingTransactionId,
                CustomerId = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                TransactionDate = new DateOnly(2026, 8, 8),
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddHours(-1)
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = missingInvoiceId,
                CustomerId = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 8, 8),
                VersionGroupId = missingInvoiceId,
                IsLatestVersion = true,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddHours(-1)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            Assert.Equal(
                1,
                await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomerContract, CustomerContractDto>(
                    sync, [contractDto], session, deviceId, businessDatabaseName));
            Assert.Equal(
                1,
                await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalItemPriceGrade, ItemPriceGradeDto>(
                    sync, [priceGradeDto], session, deviceId, businessDatabaseName));
            Assert.Equal(
                1,
                await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalTransactionAttachment, TransactionAttachmentDto>(
                    sync, [attachmentDto], session, deviceId, businessDatabaseName));
            Assert.Equal(
                1,
                await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalPayment, PaymentDto>(
                    sync, [paymentDto], session, deviceId, businessDatabaseName));

            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, contractOutbox.Id));
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, priceGradeOutbox.Id));
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, attachmentOutbox.Id));
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, paymentOutbox.Id));
            Assert.Equal("Sent", await ReadOutboxStatusAsync(db, invalidCustomerOutbox.Id));
            await db.Database.CloseConnectionAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MarkOutboxAcknowledgedForCleanEntities_AllowsExactSharedPreparedScope()
    {
        PrepareAppRoot("georaeplan-outbox-reconcile-shared-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            const string deviceId = "test-device";
            var businessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                session.SelectedBusinessDatabaseName);
            var unitId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = "공용 소유 범위 단위",
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            var outboxId = Guid.NewGuid();
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = outboxId,
                MutationId = $"{deviceId}:{nameof(LocalUnit)}:{unitId:N}:3",
                DeviceId = deviceId,
                EntityName = nameof(LocalUnit),
                EntityId = unitId,
                ExpectedRevision = 3,
                TenantCode = session.TenantCode,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = session.OfficeCode,
                BusinessDatabaseName = businessDatabaseName,
                SessionId = session.SessionId,
                UserId = session.User!.UserId,
                Status = "Sent",
                PreparedAtUtc = now.AddMinutes(-5),
                SentAtUtc = now.AddMinutes(-4)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var serverUnit = LocalMappings.ToDto(
                await db.Units.AsNoTracking().SingleAsync(unit => unit.Id == unitId));
            using var sync = CreateSyncService(db, session);
            var changed = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalUnit, UnitDto>(
                sync,
                [serverUnit],
                session,
                deviceId,
                businessDatabaseName);

            Assert.Equal(1, changed);
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, outboxId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MarkOutboxAcknowledgedForCleanEntities_AllowsVerifiedSharedItemAndParentScopes()
    {
        PrepareAppRoot("georaeplan-outbox-reconcile-shared-parent-scopes");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            const string deviceId = "test-device";
            var businessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                session.SelectedBusinessDatabaseName);
            var now = DateTime.UtcNow;
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var priceGradeOptionId = Guid.NewGuid();
            var priceGradeId = Guid.NewGuid();

            var sharedCustomer = new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "shared parent customer",
                NameMatchKey = "SHAREDPARENTCUSTOMER",
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            };
            var contract = new LocalCustomerContract
            {
                Id = contractId,
                CustomerId = customerId,
                ContractType = "shared parent contract",
                FileName = "shared-parent.pdf",
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            };
            var sharedItem = new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                NameOriginal = "shared parent item",
                NameMatchKey = "SHAREDPARENTITEM",
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            };
            var priceGradeOption = new LocalPriceGradeOption
            {
                Id = priceGradeOptionId,
                Name = "shared parent grade",
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            };
            var priceGrade = new LocalItemPriceGrade
            {
                Id = priceGradeId,
                ItemId = itemId,
                PriceGradeOptionId = priceGradeOptionId,
                PriceGradeName = priceGradeOption.Name,
                UnitPrice = 12_345m,
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            };
            db.Customers.Add(sharedCustomer);
            db.CustomerContracts.Add(contract);
            db.Items.Add(sharedItem);
            db.PriceGradeOptions.Add(priceGradeOption);
            db.ItemPriceGrades.Add(priceGrade);

            LocalSyncOutboxEntry CreateSharedCandidate(string entityName, Guid entityId)
                => new()
                {
                    Id = Guid.NewGuid(),
                    MutationId = $"{deviceId}:{entityName}:{entityId:N}:3",
                    DeviceId = deviceId,
                    EntityName = entityName,
                    EntityId = entityId,
                    ExpectedRevision = 3,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BusinessDatabaseName = businessDatabaseName,
                    SessionId = session.SessionId,
                    UserId = session.User!.UserId,
                    Status = "Sent",
                    PreparedAtUtc = now.AddMinutes(-5),
                    SentAtUtc = now.AddMinutes(-4)
                };

            var itemOutbox = CreateSharedCandidate(nameof(LocalItem), itemId);
            var contractOutbox = CreateSharedCandidate(nameof(LocalCustomerContract), contractId);
            var priceGradeOutbox = CreateSharedCandidate(nameof(LocalItemPriceGrade), priceGradeId);
            db.SyncOutboxEntries.AddRange(itemOutbox, contractOutbox, priceGradeOutbox);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            Assert.Equal(
                1,
                await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalItem, ItemDto>(
                    sync, [LocalMappings.ToDto(sharedItem)], session, deviceId, businessDatabaseName));
            Assert.Equal(
                1,
                await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomerContract, CustomerContractDto>(
                    sync, [LocalMappings.ToDto(contract)], session, deviceId, businessDatabaseName));
            Assert.Equal(
                1,
                await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalItemPriceGrade, ItemPriceGradeDto>(
                    sync, [LocalMappings.ToDto(priceGrade)], session, deviceId, businessDatabaseName));

            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, itemOutbox.Id));
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, contractOutbox.Id));
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, priceGradeOutbox.Id));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MarkOutboxAcknowledgedForCleanEntities_ReauthenticationAcknowledgesStableOwnerButKeepsDirtyLocalPending()
    {
        PrepareAppRoot("georaeplan-outbox-reconcile-reauthentication");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var originalSessionId = session.SessionId;
            var userId = session.User!.UserId;
            const string deviceId = "test-device";
            var businessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                session.SelectedBusinessDatabaseName);
            var customerId = Guid.NewGuid();
            var outboxId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "재로그인 outbox 거래처",
                NameMatchKey = "재로그인 outbox 거래처",
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now,
                Revision = 12,
                IsDirty = false
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = outboxId,
                MutationId = $"{deviceId}:{nameof(LocalCustomer)}:{customerId:N}:11",
                DeviceId = deviceId,
                EntityName = nameof(LocalCustomer),
                EntityId = customerId,
                ExpectedRevision = 11,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BusinessDatabaseName = businessDatabaseName,
                SessionId = originalSessionId,
                UserId = userId,
                Status = "Sent",
                PreparedAtUtc = now.AddMinutes(-5),
                SentAtUtc = now.AddMinutes(-4)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            session.SetSession(
                "reauthenticated-test-token",
                new UserSessionDto
                {
                    UserId = userId,
                    Username = "outbox-admin",
                    Role = "Admin",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ScopeType = TenantScopeCatalog.ScopeAdmin
                },
                DateTime.UtcNow.AddDays(1));
            Assert.NotEqual(originalSessionId, session.SessionId);

            using var sync = CreateSyncService(db, session);
            var serverCustomer = LocalMappings.ToDto(
                await db.Customers.AsNoTracking()
                    .SingleAsync(customer => customer.Id == customerId));
            var changed = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                sync,
                [serverCustomer],
                session,
                deviceId,
                businessDatabaseName);

            Assert.Equal(1, changed);
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, outboxId));
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            Assert.False(await local.HasPendingSyncChangesAsync(session));

            var dirtyCustomerId = Guid.NewGuid();
            var dirtyOutboxId = Guid.NewGuid();
            db.Customers.Add(new LocalCustomer
            {
                Id = dirtyCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "dirty 재로그인 거래처",
                NameMatchKey = "dirty 재로그인 거래처",
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now,
                Revision = 20,
                IsDirty = true
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = dirtyOutboxId,
                MutationId = $"{deviceId}:{nameof(LocalCustomer)}:{dirtyCustomerId:N}:19",
                DeviceId = deviceId,
                EntityName = nameof(LocalCustomer),
                EntityId = dirtyCustomerId,
                ExpectedRevision = 19,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BusinessDatabaseName = businessDatabaseName,
                SessionId = session.SessionId,
                UserId = userId,
                Status = "Sent",
                PreparedAtUtc = now.AddMinutes(-3),
                SentAtUtc = now.AddMinutes(-2)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var dirtyServerCustomer = LocalMappings.ToDto(
                await db.Customers.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(customer => customer.Id == dirtyCustomerId));
            var dirtyChanged = await InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<LocalCustomer, CustomerDto>(
                sync,
                [dirtyServerCustomer],
                session,
                deviceId,
                businessDatabaseName);

            Assert.Equal(0, dirtyChanged);
            Assert.Equal("Sent", await ReadOutboxStatusAsync(db, dirtyOutboxId));
            Assert.True(await local.HasPendingSyncChangesAsync(session));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TrySyncAsync_ReauthenticationRecoversCleanEntityWithSentOutbox()
    {
        PrepareAppRoot("georaeplan-outbox-only-runtime-reconciliation");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var originalSessionId = session.SessionId;
            var userId = session.User!.UserId;
            var customerId = Guid.NewGuid();
            var outboxId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            const string deviceId = "outbox-only-runtime-device";
            var businessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                session.SelectedBusinessDatabaseName);
            var customer = new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "outbox only runtime customer",
                NameMatchKey = "OUTBOXONLYRUNTIMECUSTOMER",
                Revision = 12,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            };
            db.Customers.Add(customer);
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = outboxId,
                MutationId = $"{deviceId}:{nameof(LocalCustomer)}:{customerId:N}:11",
                DeviceId = deviceId,
                EntityName = nameof(LocalCustomer),
                EntityId = customerId,
                ExpectedRevision = 11,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BusinessDatabaseName = businessDatabaseName,
                SessionId = originalSessionId,
                UserId = userId,
                Status = "Sent",
                PreparedAtUtc = now.AddMinutes(-5),
                SentAtUtc = now.AddMinutes(-4)
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "Sync.DeviceId",
                Value = deviceId
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            session.SetSession(
                "outbox-only-reauthenticated-token",
                new UserSessionDto
                {
                    UserId = userId,
                    Username = "outbox-admin",
                    Role = "Admin",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ScopeType = TenantScopeCatalog.ScopeAdmin
                },
                DateTime.UtcNow.AddDays(1));
            Assert.NotEqual(originalSessionId, session.SessionId);

            var serverCustomer = LocalMappings.ToDto(customer);
            var handler = new OutboxReconciliationEchoHandler(serverCustomer);
            using var sync = CreateSyncService(db, session, handler);
            var candidateLoadCount = 0;
            sync.EligibleOutboxReconciliationCandidateLoadStartedForTesting =
                () => candidateLoadCount++;

            Assert.True(await sync.TrySyncAsync()
                .WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.True(handler.PullCount > 0);
            Assert.Equal(5, candidateLoadCount);
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, outboxId));
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            Assert.False(await local.HasPendingSyncChangesAsync(session));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrySyncAsync_InitialCandidateOpensPull_LateCandidateAcknowledgementRequiresPullPayloadRevisionAndOwnerProof(
        bool firstPullIncludesLateCandidate)
    {
        PrepareAppRoot("georaeplan-outbox-runtime-snapshot-late-insert");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var initialCustomerId = Guid.NewGuid();
            var lateCustomerId = Guid.NewGuid();
            var initialOutboxId = Guid.NewGuid();
            var lateOutboxId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            const string deviceId = "outbox-runtime-late-device";
            var businessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                session.SelectedBusinessDatabaseName);
            var initialCustomer = new LocalCustomer
            {
                Id = initialCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "initial snapshot customer",
                NameMatchKey = "INITIALSNAPSHOTCUSTOMER",
                Revision = 12,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            };
            var lateCustomer = new LocalCustomer
            {
                Id = lateCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "late snapshot customer",
                NameMatchKey = "LATESNAPSHOTCUSTOMER",
                Revision = 12,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            };
            db.Customers.AddRange(initialCustomer, lateCustomer);
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = initialOutboxId,
                MutationId =
                    $"{deviceId}:{nameof(LocalCustomer)}:{initialCustomerId:N}:11",
                DeviceId = deviceId,
                EntityName = nameof(LocalCustomer),
                EntityId = initialCustomerId,
                ExpectedRevision = 11,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BusinessDatabaseName = businessDatabaseName,
                SessionId = session.SessionId,
                UserId = session.User!.UserId,
                Status = "Sent",
                PreparedAtUtc = now.AddMinutes(-6),
                SentAtUtc = now.AddMinutes(-5)
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "Sync.DeviceId",
                Value = deviceId
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var persistedCustomers = await db.Customers.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(customer =>
                    customer.Id == initialCustomerId ||
                    customer.Id == lateCustomerId)
                .ToDictionaryAsync(customer => customer.Id);
            var initialServerCustomer = LocalMappings.ToDto(
                persistedCustomers[initialCustomerId]);
            var lateServerCustomer = LocalMappings.ToDto(
                persistedCustomers[lateCustomerId]);
            var firstPullCustomers = firstPullIncludesLateCandidate
                ? new[] { initialServerCustomer, lateServerCustomer }
                : new[] { initialServerCustomer };
            var handler = new OutboxReconciliationSequenceHandler(
                firstPullCustomers,
                [lateServerCustomer]);
            using var sync = CreateSyncService(db, session, handler);
            var candidateLoadCount = 0;
            var inserted = false;
            sync.EligibleOutboxReconciliationCandidateLoadStartedForTesting =
                () => candidateLoadCount++;
            sync.AfterInitialOutboxReconciliationCandidateSnapshotLoadedAsyncForTesting =
                async ct =>
                {
                    if (inserted)
                        return;

                    inserted = true;
                    await using var concurrentDb = new LocalDbContext();
                    concurrentDb.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
                    {
                        Id = lateOutboxId,
                        MutationId =
                            $"{deviceId}:{nameof(LocalCustomer)}:{lateCustomerId:N}:11",
                        DeviceId = deviceId,
                        EntityName = nameof(LocalCustomer),
                        EntityId = lateCustomerId,
                        ExpectedRevision = 11,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        BusinessDatabaseName = businessDatabaseName,
                        SessionId = session.SessionId,
                        UserId = session.User!.UserId,
                        Status = "Sent",
                        PreparedAtUtc = now.AddMinutes(-5),
                        SentAtUtc = now.AddMinutes(-4)
                    });
                    await concurrentDb.SaveChangesAsync(ct);
                };

            Assert.Equal(
                firstPullIncludesLateCandidate,
                await sync.TrySyncAsync()
                    .WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(
                "Acknowledged",
                await ReadOutboxStatusAsync(db, initialOutboxId));
            var loadsAfterFirstRun = candidateLoadCount;
            Assert.InRange(loadsAfterFirstRun, 3, 5);

            if (firstPullIncludesLateCandidate)
            {
                var acknowledgedLateOutbox = await db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(entry => entry.Id == lateOutboxId);
                Assert.Equal("Acknowledged", acknowledgedLateOutbox.Status);
                Assert.Equal(
                    lateServerCustomer.Revision,
                    acknowledgedLateOutbox.AcceptedRevision);
                Assert.Equal(session.User.UserId, acknowledgedLateOutbox.UserId);
                Assert.Equal(deviceId, acknowledgedLateOutbox.DeviceId);
                Assert.Equal(
                    businessDatabaseName,
                    acknowledgedLateOutbox.BusinessDatabaseName);
                return;
            }

            Assert.Equal(
                "Sent",
                await ReadOutboxStatusAsync(db, lateOutboxId));

            Assert.True(await sync.TrySyncAsync()
                .WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(
                "Acknowledged",
                await ReadOutboxStatusAsync(db, lateOutboxId));
            Assert.InRange(
                candidateLoadCount - loadsAfterFirstRun,
                3,
                5);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TrySyncAsync_SourceOfficeRecoversOutboxForCrossOfficeTransfer()
    {
        PrepareAppRoot("georaeplan-transfer-outbox-only-source-reconciliation");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateOnlineOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            var originalSessionId = session.SessionId;
            var userId = session.User!.UserId;
            var transferId = Guid.NewGuid();
            var outboxId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            const string deviceId = "transfer-outbox-source-device";
            var businessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                session.SelectedBusinessDatabaseName);
            var transfer = new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "TRANSFER-OUTBOX-SOURCE",
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                TransferStatus = "수령대기",
                CreatedByUsername = session.User.Username,
                LastSavedByUsername = session.User.Username,
                Revision = 12,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            };
            db.InventoryTransfers.Add(transfer);
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = outboxId,
                MutationId = $"{deviceId}:{nameof(LocalInventoryTransfer)}:{transferId:N}:11",
                DeviceId = deviceId,
                EntityName = nameof(LocalInventoryTransfer),
                EntityId = transferId,
                ExpectedRevision = 11,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                BusinessDatabaseName = businessDatabaseName,
                SessionId = originalSessionId,
                UserId = userId,
                Status = "Sent",
                PreparedAtUtc = now.AddMinutes(-5),
                SentAtUtc = now.AddMinutes(-4)
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "Sync.DeviceId",
                Value = deviceId
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            session.SetSession(
                "transfer-outbox-reauthenticated-token",
                new UserSessionDto
                {
                    UserId = userId,
                    Username = "usenet-online-user",
                    Role = DomainConstants.RoleUser,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ScopeType = TenantScopeCatalog.ScopeOfficeOnly
                },
                DateTime.UtcNow.AddDays(1));
            Assert.NotEqual(originalSessionId, session.SessionId);

            var handler = new OutboxTransferReconciliationEchoHandler(
                LocalMappings.ToDto(transfer));
            using var sync = CreateSyncService(db, session, handler);

            Assert.True(await sync.TrySyncAsync()
                .WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.True(handler.PullCount > 0);
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, outboxId));
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            Assert.False(await local.HasPendingSyncChangesAsync(session));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TrySyncAsync_StoredOfficeOutbox_ReusesOneCandidateSnapshot()
    {
        PrepareAppRoot("georaeplan-outbox-runtime-stored-office-snapshot");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateOnlineOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Usenet);
            var storedUser = new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = "yeonsu-stored-sync-user",
                Role = DomainConstants.RoleUser,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            };
            var customerId = Guid.NewGuid();
            var outboxId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            const string deviceId = "outbox-runtime-stored-device";
            var businessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                session.SelectedBusinessDatabaseName);
            var customer = new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "stored office snapshot customer",
                NameMatchKey = "STOREDOFFICESNAPSHOTCUSTOMER",
                Revision = 12,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            };
            db.Customers.Add(customer);
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = outboxId,
                MutationId =
                    $"{deviceId}:{nameof(LocalCustomer)}:{customerId:N}:11",
                DeviceId = deviceId,
                EntityName = nameof(LocalCustomer),
                EntityId = customerId,
                ExpectedRevision = 11,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                BusinessDatabaseName = businessDatabaseName,
                SessionId = Guid.NewGuid(),
                UserId = storedUser.UserId,
                Status = "Sent",
                PreparedAtUtc = now.AddMinutes(-5),
                SentAtUtc = now.AddMinutes(-4)
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "Sync.DeviceId",
                Value = deviceId
            });
            await db.SaveChangesAsync();

            var dispatcher = new SyncRequestDispatcher();
            var credentialLocal = new LocalStateService(
                db,
                new OfficeAccessService(),
                dispatcher,
                session);
            await credentialLocal.SaveOfficeSyncCredentialAsync(
                storedUser,
                storedUser.Username,
                "test-stored-password");
            db.ChangeTracker.Clear();

            var persistedCustomer = await db.Customers.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(entity => entity.Id == customerId);

            var scenario = new StoredOfficeOutboxReconciliationScenario(
                storedUser,
                LocalMappings.ToDto(persistedCustomer));
            using var sync = CreateSyncService(
                db,
                session,
                scenario.MainHandler,
                httpClientFactory: scenario.OfficeHttpClientFactory);
            var candidateLoadCount = 0;
            sync.EligibleOutboxReconciliationCandidateLoadStartedForTesting =
                () => candidateLoadCount++;

            Assert.True(await sync.TrySyncAsync()
                .WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(1, scenario.LoginCount);
            Assert.Equal(1, scenario.OfficePullCount);
            Assert.Equal(3, candidateLoadCount);
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, outboxId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task OutboxOnlyRuntimeGate_IgnoresUnreconcilableOwnerOrEntityRows()
    {
        PrepareAppRoot("georaeplan-outbox-only-runtime-negative-candidates");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            const string deviceId = "outbox-runtime-negative-device";
            var businessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                session.SelectedBusinessDatabaseName);
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "outbox runtime negative customer",
                NameMatchKey = "OUTBOXRUNTIMENEGATIVECUSTOMER",
                Revision = 12,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            });

            LocalSyncOutboxEntry CreateCandidate(
                string suffix,
                string entityName = nameof(LocalCustomer),
                Guid? entityId = null,
                string? responsibleOfficeCode = null,
                string? candidateDeviceId = null,
                string? candidateBusinessDatabaseName = null,
                Guid? userId = null,
                long expectedRevision = 11) => new()
                {
                    Id = Guid.NewGuid(),
                    MutationId = $"{deviceId}:{suffix}",
                    DeviceId = candidateDeviceId ?? deviceId,
                    EntityName = entityName,
                    EntityId = entityId ?? customerId,
                    ExpectedRevision = expectedRevision,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = responsibleOfficeCode ?? OfficeCodeCatalog.Usenet,
                    BusinessDatabaseName = candidateBusinessDatabaseName ?? businessDatabaseName,
                    SessionId = session.SessionId,
                    UserId = userId ?? session.User!.UserId,
                    Status = "Sent",
                    PreparedAtUtc = now.AddMinutes(-5),
                    SentAtUtc = now.AddMinutes(-4)
                };

            db.SyncOutboxEntries.AddRange(
                CreateCandidate("blank-responsible", responsibleOfficeCode: string.Empty),
                CreateCandidate("empty-user", userId: Guid.Empty),
                CreateCandidate("unknown-entity", entityName: "LocalFutureEntity"),
                CreateCandidate("empty-entity-id", entityId: Guid.Empty),
                CreateCandidate("missing-local", entityId: Guid.NewGuid()),
                CreateCandidate("other-device", candidateDeviceId: "other-device"),
                CreateCandidate("other-database", candidateBusinessDatabaseName: "ITWORLD"),
                CreateCandidate("revision-not-advanced", expectedRevision: 12));
            db.Settings.Add(new LocalSetting
            {
                Key = "Sync.DeviceId",
                Value = deviceId
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            Assert.Empty(await InvokeLoadEligibleOutboxReconciliationCandidatesAsync(sync));
            Assert.False(await InvokeHasPendingOutboxForSessionAsync(sync, session));
            Assert.Empty(await InvokeGetPendingReconciliationOfficeSummariesAsync(sync));
            Assert.All(
                await db.SyncOutboxEntries.AsNoTracking().ToListAsync(),
                row => Assert.Equal("Sent", row.Status));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncOutboxDiagnosticsOperations_AreLimitedToCurrentSessionScope()
    {
        PrepareAppRoot("georaeplan-outbox-session-scope-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetSession = CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), usenetSession);
            var usenetFailedId = Guid.NewGuid();
            var itworldFailedId = Guid.NewGuid();
            var usenetAcknowledgedId = Guid.NewGuid();
            var itworldAcknowledgedId = Guid.NewGuid();
            db.SyncOutboxEntries.AddRange(
                CreateOutboxEntry(
                    "Failed",
                    entryId: usenetFailedId,
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet,
                    responsibleOfficeCode: OfficeCodeCatalog.Usenet),
                CreateOutboxEntry(
                    "Failed",
                    entryId: itworldFailedId,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld),
                CreateOutboxEntry(
                    "Acknowledged",
                    entryId: usenetAcknowledgedId,
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet,
                    responsibleOfficeCode: OfficeCodeCatalog.Usenet),
                CreateOutboxEntry(
                    "Acknowledged",
                    entryId: itworldAcknowledgedId,
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld));
            await db.SaveChangesAsync();

            var scopedEntries = await local.GetSyncOutboxEntriesAsync(usenetSession, 20);
            Assert.Equal(2, scopedEntries.Count);
            Assert.All(scopedEntries, entry => Assert.Equal(OfficeCodeCatalog.Usenet, entry.ResponsibleOfficeCode));

            var scopedSummary = await local.GetSyncOutboxSummaryAsync(usenetSession);
            Assert.Equal(2, scopedSummary.TotalCount);
            Assert.Equal(1, scopedSummary.FailedCount);
            Assert.Equal(1, scopedSummary.AcknowledgedCount);

            Assert.Equal(0, await local.ResetSyncOutboxEntriesForRetryAsync([itworldFailedId], usenetSession));
            Assert.Equal("Failed", await ReadOutboxStatusAsync(db, itworldFailedId));

            Assert.Equal(1, await local.ResetAllPendingSyncOutboxEntriesForRetryAsync(usenetSession));
            Assert.Equal("Prepared", await ReadOutboxStatusAsync(db, usenetFailedId));
            Assert.Equal("Failed", await ReadOutboxStatusAsync(db, itworldFailedId));

            Assert.Equal(1, await local.ClearAcknowledgedSyncOutboxEntriesAsync(usenetSession));
            Assert.False(await db.SyncOutboxEntries.AsNoTracking().AnyAsync(entry => entry.Id == usenetAcknowledgedId));
            Assert.True(await db.SyncOutboxEntries.AsNoTracking().AnyAsync(entry => entry.Id == itworldAcknowledgedId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task LocalIntegrityReport_OutboxIssuesAreLimitedToCurrentSessionScope()
    {
        PrepareAppRoot("georaeplan-integrity-outbox-session-scope-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var usenetSession = CreateOfficeSession(TenantScopeCatalog.UsenetGroup, OfficeCodeCatalog.Usenet);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), usenetSession);
            var staleSentAtUtc = DateTime.UtcNow.AddMinutes(-30);
            db.SyncOutboxEntries.AddRange(
                CreateOutboxEntry(
                    "Failed",
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet,
                    responsibleOfficeCode: OfficeCodeCatalog.Usenet),
                CreateOutboxEntry(
                    "Failed",
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld),
                CreateOutboxEntry(
                    "Sent",
                    tenantCode: TenantScopeCatalog.UsenetGroup,
                    officeCode: OfficeCodeCatalog.Usenet,
                    responsibleOfficeCode: OfficeCodeCatalog.Usenet,
                    sentAtUtc: staleSentAtUtc),
                CreateOutboxEntry(
                    "Sent",
                    tenantCode: TenantScopeCatalog.Itworld,
                    officeCode: OfficeCodeCatalog.Itworld,
                    responsibleOfficeCode: OfficeCodeCatalog.Itworld,
                    sentAtUtc: staleSentAtUtc));
            await db.SaveChangesAsync();

            var report = await local.BuildIntegrityReportAsync(usenetSession);

            var failedIssue = Assert.Single(report.Issues, issue => issue.Code == "sync_outbox_failed_pending");
            Assert.Equal(1, failedIssue.Count);
            var staleIssue = Assert.Single(report.Issues, issue => issue.Code == "sync_outbox_sent_stuck");
            Assert.Equal(1, staleIssue.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_ForbiddenPush_KeepsDirtyAndRecordsReadableOutboxFailure()
    {
        const string permissionMessage = "현재 계정 권한으로 서버 동기화 반영이 허용되지 않는 변경이 포함되어 있습니다: 환경설정/분류";
        PrepareAppRoot("georaeplan-forbidden-push-outbox-guard");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.Parse("99999999-aaaa-bbbb-cccc-dddddddddddd");
            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = "권한 거부 테스트 단위",
                IsActive = true,
                Revision = 0,
                IsDirty = true,
                CreatedAtUtc = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new ForbiddenPushThenEmptyPullHandler(permissionMessage);
            using var sync = CreateSyncService(db, session, handler);

            var synced = await sync.FlushPendingChangesAsync();

            Assert.False(synced);
            Assert.Equal(1, handler.PushCount);
            Assert.True(await db.Units.IgnoreQueryFilters().AsNoTracking().AnyAsync(unit => unit.Id == unitId && unit.IsDirty));

            var outbox = await db.SyncOutboxEntries.AsNoTracking().SingleAsync();
            Assert.Equal("Failed", outbox.Status);
            Assert.Contains(permissionMessage, outbox.ErrorMessage);
            Assert.DoesNotContain("{\"message\"", outbox.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\u", outbox.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            var lastError = await db.Settings
                .AsNoTracking()
                .Where(setting => setting.Key == "Sync.LastError")
                .Select(setting => setting.Value)
                .SingleAsync();
            Assert.Contains("동기화 업로드(sync/push) 실패", lastError);
            Assert.Contains(permissionMessage, lastError);
            Assert.DoesNotContain("{\"message\"", lastError, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\\u", lastError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task MarkOutboxAcknowledged_SupersedesOnlyOlderMutationsForAcceptedEntity()
    {
        PrepareAppRoot("georaeplan-outbox-supersede-scope");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var entityId = Guid.NewGuid();
            var unrelatedEntityId = Guid.NewGuid();
            var preparedAtUtc = DateTime.UtcNow;
            const string oldMutationId = "test-device:LocalItem:0-old";
            const string sameTimestampLexicallyEarlierMutationId = "test-device:LocalItem:a-same-time-earlier";
            const string currentMutationId = "test-device:LocalItem:m-current";
            const string sameTimestampLexicallyLaterMutationId = "test-device:LocalItem:z-same-time-later";
            const string differentDatabaseMutationId = "test-device:LocalItem:0-different-database";
            const string differentTenantMutationId = "test-device:LocalItem:0-different-tenant";
            const string differentOfficeMutationId = "test-device:LocalItem:0-different-office";
            const string differentResponsibleOfficeMutationId = "test-device:LocalItem:0-different-responsible-office";
            const string differentDeviceMutationId = "other-device:LocalItem:0-old";
            const string differentSessionMutationId = "test-device:LocalItem:0-different-session";
            const string differentUserMutationId = "test-device:LocalItem:0-different-user";
            const string legacyMissingScopeMutationId = "test-device:LocalItem:0-legacy-missing-scope";
            const string unrelatedMutationId = "test-device:LocalItem:0-unrelated";

            db.Items.Add(new LocalItem
            {
                Id = entityId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "supersede anchor item",
                NameMatchKey = "supersedeanchoritem",
                Revision = 1,
                IsDirty = true,
                CreatedAtUtc = preparedAtUtc.AddHours(-1),
                UpdatedAtUtc = preparedAtUtc
            });

            db.SyncOutboxEntries.AddRange(
                CreateScopedOutbox(oldMutationId, entityId, "Failed", preparedAtUtc.AddMinutes(-2), session),
                CreateScopedOutbox(sameTimestampLexicallyEarlierMutationId, entityId, "Failed", preparedAtUtc, session),
                CreateScopedOutbox(currentMutationId, entityId, "Sent", preparedAtUtc, session),
                CreateScopedOutbox(sameTimestampLexicallyLaterMutationId, entityId, "Prepared", preparedAtUtc, session),
                CreateScopedOutbox(
                    differentDatabaseMutationId,
                    entityId,
                    "Failed",
                    preparedAtUtc.AddMinutes(-2),
                    session,
                    businessDatabaseName: "ITWORLD"),
                CreateScopedOutbox(
                    differentTenantMutationId,
                    entityId,
                    "Failed",
                    preparedAtUtc.AddMinutes(-2),
                    session,
                    tenantCode: TenantScopeCatalog.Itworld),
                CreateScopedOutbox(
                    differentOfficeMutationId,
                    entityId,
                    "Failed",
                    preparedAtUtc.AddMinutes(-2),
                    session,
                    officeCode: OfficeCodeCatalog.Yeonsu),
                CreateScopedOutbox(
                    differentResponsibleOfficeMutationId,
                    entityId,
                    "Failed",
                    preparedAtUtc.AddMinutes(-2),
                    session,
                    responsibleOfficeCode: OfficeCodeCatalog.Yeonsu),
                CreateScopedOutbox(
                    differentDeviceMutationId,
                    entityId,
                    "Failed",
                    preparedAtUtc.AddMinutes(-2),
                    session,
                    deviceId: "other-device"),
                CreateScopedOutbox(
                    differentSessionMutationId,
                    entityId,
                    "Failed",
                    preparedAtUtc.AddMinutes(-2),
                    session,
                    sessionId: Guid.NewGuid()),
                CreateScopedOutbox(
                    differentUserMutationId,
                    entityId,
                    "Failed",
                    preparedAtUtc.AddMinutes(-2),
                    session,
                    userId: Guid.NewGuid()),
                CreateScopedOutbox(
                    legacyMissingScopeMutationId,
                    entityId,
                    "Failed",
                    preparedAtUtc.AddMinutes(-2),
                    session,
                    businessDatabaseName: string.Empty,
                    sessionId: Guid.Empty,
                    userId: Guid.Empty),
                CreateScopedOutbox(unrelatedMutationId, unrelatedEntityId, "Failed", preparedAtUtc.AddMinutes(-2), session));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var scopedRows = await db.SyncOutboxEntries
                .AsNoTracking()
                .Where(entry =>
                    entry.MutationId == oldMutationId ||
                    entry.MutationId == currentMutationId)
                .ToDictionaryAsync(entry => entry.MutationId);
            var sameScopeMethod = typeof(SyncService).GetMethod(
                "HasProvablySameOutboxSupersedeScope",
                BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(
                    nameof(SyncService),
                    "HasProvablySameOutboxSupersedeScope");
            Assert.True((bool)sameScopeMethod.Invoke(
                null,
                [scopedRows[oldMutationId], scopedRows[currentMutationId]])!);
            Assert.True(
                scopedRows[oldMutationId].PreparedAtUtc <
                scopedRows[currentMutationId].PreparedAtUtc);

            var preparedItem = LocalMappings.ToDto(
                await db.Items.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(item => item.Id == entityId));
            preparedItem.ExpectedRevision = 1;
            preparedItem.MutationId = currentMutationId;
            preparedItem.MutationCreatedAtUtc = preparedAtUtc;

            using var sync = CreateSyncService(db, session);
            await InvokeMarkOutboxAcknowledgedAsync(
                sync,
                new SyncPushRequest
                {
                    DeviceId = "test-device",
                    Items = [preparedItem]
                },
                [
                    new SyncAcceptedRevisionDto
                    {
                        EntityName = "Item",
                        EntityId = entityId,
                        Revision = 2,
                        UpdatedAtUtc = preparedAtUtc
                    }
                ]);

            var statusByMutationId = await db.SyncOutboxEntries
                .AsNoTracking()
                .ToDictionaryAsync(entry => entry.MutationId, entry => entry.Status);
            Assert.Equal("Acknowledged", statusByMutationId[currentMutationId]);
            Assert.Equal("Acknowledged", statusByMutationId[oldMutationId]);
            Assert.Equal("Failed", statusByMutationId[sameTimestampLexicallyEarlierMutationId]);
            Assert.Equal("Prepared", statusByMutationId[sameTimestampLexicallyLaterMutationId]);
            Assert.Equal("Failed", statusByMutationId[differentDatabaseMutationId]);
            Assert.Equal("Failed", statusByMutationId[differentTenantMutationId]);
            Assert.Equal("Failed", statusByMutationId[differentOfficeMutationId]);
            Assert.Equal("Failed", statusByMutationId[differentResponsibleOfficeMutationId]);
            Assert.Equal("Failed", statusByMutationId[differentDeviceMutationId]);
            Assert.Equal("Failed", statusByMutationId[differentSessionMutationId]);
            Assert.Equal("Failed", statusByMutationId[differentUserMutationId]);
            Assert.Equal("Failed", statusByMutationId[legacyMissingScopeMutationId]);
            Assert.Equal("Failed", statusByMutationId[unrelatedMutationId]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("session")]
    [InlineData("prepared-at")]
    public async Task MarkOutboxAcknowledged_OlderAnchorTupleChangedAfterRefreshIsNotSuperseded(
        string changedField)
    {
        PrepareAppRoot($"georaeplan-outbox-supersede-tuple-race-{changedField}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var preparedAtUtc = DateTime.UtcNow.AddMinutes(-1);
            const string oldMutationId = "test-device:LocalItem:tuple-race-old";
            const string currentMutationId = "test-device:LocalItem:tuple-race-current";
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "supersede tuple race item",
                NameMatchKey = "supersedetupleraceitem",
                Revision = 1,
                IsDirty = true,
                CreatedAtUtc = preparedAtUtc.AddHours(-1),
                UpdatedAtUtc = preparedAtUtc
            });
            var oldRow = CreateScopedOutbox(
                oldMutationId,
                itemId,
                "Failed",
                preparedAtUtc.AddMinutes(-2),
                session);
            var currentRow = CreateScopedOutbox(
                currentMutationId,
                itemId,
                "Sent",
                preparedAtUtc,
                session);
            db.SyncOutboxEntries.AddRange(oldRow, currentRow);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var preparedItem = LocalMappings.ToDto(
                await db.Items.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(item => item.Id == itemId));
            preparedItem.ExpectedRevision = 1;
            preparedItem.MutationId = currentMutationId;
            preparedItem.MutationCreatedAtUtc = preparedAtUtc;
            using var sync = CreateSyncService(db, session);
            var changedSessionId = Guid.NewGuid();
            sync.BeforeOutboxSupersedeUpdateAsyncForTesting =
                async (rowId, ct) =>
                {
                    if (rowId != oldRow.Id)
                        return;

                    await using var concurrentDb = new LocalDbContext();
                    var concurrent = await concurrentDb.SyncOutboxEntries
                        .SingleAsync(entry => entry.Id == rowId, ct);
                    switch (changedField)
                    {
                        case "session":
                            concurrent.SessionId = changedSessionId;
                            break;
                        case "prepared-at":
                            concurrent.PreparedAtUtc =
                                preparedAtUtc.AddMinutes(1);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(
                                nameof(changedField));
                    }
                    await concurrentDb.SaveChangesAsync(ct);
                };

            await InvokeMarkOutboxAcknowledgedAsync(
                sync,
                new SyncPushRequest
                {
                    DeviceId = "test-device",
                    Items = [preparedItem]
                },
                [
                    new SyncAcceptedRevisionDto
                    {
                        EntityName = "Item",
                        EntityId = itemId,
                        Revision = 2,
                        UpdatedAtUtc = preparedAtUtc.AddMinutes(1)
                    }
                ]);

            var current = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.Id == currentRow.Id);
            var preserved = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.Id == oldRow.Id);
            Assert.Equal("Acknowledged", current.Status);
            Assert.Equal("Failed", preserved.Status);
            if (changedField == "session")
                Assert.Equal(changedSessionId, preserved.SessionId);
            if (changedField == "prepared-at")
                Assert.Equal(
                    preparedAtUtc.AddMinutes(1),
                    preserved.PreparedAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("Prepared")]
    [InlineData("Failed")]
    public async Task MarkOutboxAcknowledged_CurrentAnchorMustBeSent(
        string status)
    {
        PrepareAppRoot($"georaeplan-outbox-current-anchor-{status}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var updatedAtUtc = DateTime.UtcNow.AddMinutes(-2);
            var mutationId = $"test-device:{nameof(LocalItem)}:status-{status}";
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "anchor status item",
                NameMatchKey = "anchorstatusitem",
                Revision = 1,
                IsDirty = true,
                CreatedAtUtc = updatedAtUtc.AddHours(-1),
                UpdatedAtUtc = updatedAtUtc
            });
            var outbox = CreateScopedOutbox(
                mutationId,
                itemId,
                status,
                updatedAtUtc,
                session);
            db.SyncOutboxEntries.Add(outbox);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var preparedItem = LocalMappings.ToDto(
                await db.Items.IgnoreQueryFilters().AsNoTracking()
                    .SingleAsync(item => item.Id == itemId));
            preparedItem.ExpectedRevision = 1;
            preparedItem.MutationId = mutationId;
            preparedItem.MutationCreatedAtUtc = updatedAtUtc;
            using var sync = CreateSyncService(db, session);
            await InvokeMarkOutboxAcknowledgedAsync(
                sync,
                new SyncPushRequest
                {
                    DeviceId = "test-device",
                    Items = [preparedItem]
                },
                [
                    new SyncAcceptedRevisionDto
                    {
                        EntityName = "Item",
                        EntityId = itemId,
                        Revision = 2,
                        UpdatedAtUtc = updatedAtUtc.AddMinutes(1)
                    }
                ]);

            var preserved = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.Id == outbox.Id);
            Assert.Equal(status, preserved.Status);
            Assert.Null(preserved.AcknowledgedAtUtc);
            Assert.Equal(0, preserved.AcceptedRevision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("user")]
    [InlineData("session")]
    [InlineData("device")]
    [InlineData("database")]
    [InlineData("tenant")]
    [InlineData("office")]
    [InlineData("responsible-office")]
    [InlineData("expected-revision")]
    public async Task TrySyncAsync_ExistingMutationIdWithoutExactCurrentOwnerReceiptBlocksBeforePush(
        string mismatch)
    {
        PrepareAppRoot($"georaeplan-outbox-prepush-receipt-{mismatch}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            const string deviceId = "receipt-device";
            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var updatedAtUtc = new DateTime(
                2026,
                8,
                9,
                2,
                15,
                0,
                DateTimeKind.Utc);
            const long expectedRevision = 3;
            var mutationId =
                $"{deviceId}:{nameof(LocalUnit)}:{unitId:N}:" +
                $"{expectedRevision}:{updatedAtUtc.Ticks}:0";
            db.Settings.Add(new LocalSetting
            {
                Key = "Sync.DeviceId",
                Value = deviceId
            });
            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = "receipt preflight unit",
                IsActive = true,
                Revision = expectedRevision,
                IsDirty = true,
                CreatedAtUtc = updatedAtUtc.AddHours(-1),
                UpdatedAtUtc = updatedAtUtc
            });
            var preservedAcceptedAtUtc = updatedAtUtc.AddMinutes(-1);
            var outboxId = Guid.NewGuid();
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = outboxId,
                MutationId = mutationId,
                DeviceId = mismatch == "device" ? "other-device" : deviceId,
                EntityName = nameof(LocalUnit),
                EntityId = unitId,
                ExpectedRevision = mismatch == "expected-revision"
                    ? expectedRevision - 1
                    : expectedRevision,
                TenantCode = mismatch == "tenant"
                    ? TenantScopeCatalog.Itworld
                    : TenantScopeCatalog.UsenetGroup,
                OfficeCode = mismatch == "office"
                    ? OfficeCodeCatalog.Yeonsu
                    : OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = mismatch == "responsible-office"
                    ? OfficeCodeCatalog.Yeonsu
                    : OfficeCodeCatalog.Usenet,
                BusinessDatabaseName = mismatch == "database"
                    ? "ITWORLD"
                    : "USENET",
                SessionId = mismatch == "session"
                    ? Guid.NewGuid()
                    : session.SessionId,
                UserId = mismatch == "user"
                    ? Guid.NewGuid()
                    : session.User!.UserId,
                Status = "Failed",
                ErrorMessage = "preserve external failure",
                PreparedAtUtc = updatedAtUtc.AddMinutes(-2),
                AcceptedRevision = 17,
                AcceptedUpdatedAtUtc = preservedAcceptedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new AuthoritativePullOnlyHandler(
                new SyncPullResponse { CurrentServerRevision = 3 });
            using var sync = CreateSyncService(db, session, handler);

            Assert.False(await sync.TrySyncAsync());
            Assert.Equal(0, handler.PushCount);

            var preserved = await db.SyncOutboxEntries
                .AsNoTracking()
                .SingleAsync(entry => entry.Id == outboxId);
            Assert.Equal("Failed", preserved.Status);
            Assert.Equal("preserve external failure", preserved.ErrorMessage);
            Assert.Equal(17, preserved.AcceptedRevision);
            Assert.Equal(preservedAcceptedAtUtc, preserved.AcceptedUpdatedAtUtc);
            Assert.True(await db.Units.IgnoreQueryFilters()
                .Where(unit => unit.Id == unitId)
                .Select(unit => unit.IsDirty)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("user")]
    [InlineData("session")]
    [InlineData("device")]
    [InlineData("database")]
    [InlineData("scope")]
    public async Task TrySyncAsync_OutboxAnchorChangedDuringHttpIsNotSentFailedOrAcknowledged(
        string changedField)
    {
        PrepareAppRoot($"georaeplan-outbox-http-receipt-race-{changedField}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var updatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = "http receipt race unit",
                IsActive = true,
                Revision = 4,
                IsDirty = true,
                CreatedAtUtc = updatedAtUtc.AddHours(-1),
                UpdatedAtUtc = updatedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPushAckThenEmptyPullHandler(
                unitId,
                entityName: "Unit",
                acceptedRevision: 5,
                acceptedUpdatedAtUtc: updatedAtUtc.AddMinutes(1));
            using var sync = CreateSyncService(db, session, handler);
            var syncTask = sync.TrySyncAsync();
            var pushed = await handler.PushReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(15));
            var pushedUnit = Assert.Single(pushed.Units);

            var preservedAcceptedAtUtc = updatedAtUtc.AddMinutes(2);
            await using (var concurrentDb = new LocalDbContext())
            {
                var outbox = await concurrentDb.SyncOutboxEntries
                    .SingleAsync(entry =>
                        entry.MutationId == pushedUnit.MutationId);
                switch (changedField)
                {
                    case "user":
                        outbox.UserId = Guid.NewGuid();
                        break;
                    case "session":
                        outbox.SessionId = Guid.NewGuid();
                        break;
                    case "device":
                        outbox.DeviceId = "other-device";
                        break;
                    case "database":
                        outbox.BusinessDatabaseName = "ITWORLD";
                        break;
                    case "scope":
                        outbox.OfficeCode = OfficeCodeCatalog.Yeonsu;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(changedField));
                }

                outbox.ErrorMessage = "preserve concurrent owner change";
                outbox.AcceptedRevision = 91;
                outbox.AcceptedUpdatedAtUtc = preservedAcceptedAtUtc;
                await concurrentDb.SaveChangesAsync();
            }

            handler.ReleasePush();
            Assert.False(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(1, handler.PushCount);

            db.ChangeTracker.Clear();
            var preserved = await db.SyncOutboxEntries
                .AsNoTracking()
                .SingleAsync(entry => entry.MutationId == pushedUnit.MutationId);
            Assert.Equal("Prepared", preserved.Status);
            Assert.Equal("preserve concurrent owner change", preserved.ErrorMessage);
            Assert.Equal(91, preserved.AcceptedRevision);
            Assert.Equal(preservedAcceptedAtUtc, preserved.AcceptedUpdatedAtUtc);
            Assert.True(await db.Units.IgnoreQueryFilters()
                .Where(unit => unit.Id == unitId)
                .Select(unit => unit.IsDirty)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TrySyncAsync_RentalBillingProfileAcceptedPurgeRevisionEqualToLocalRevision_AcknowledgesOutboxAndConverges(
        bool receiptExistsBeforePush)
    {
        PrepareAppRoot(
            $"georaeplan-rental-profile-accepted-purge-{receiptExistsBeforePush}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var profileId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var exactOutboxId = Guid.NewGuid();
            var unrelatedUnitId = Guid.NewGuid();
            var unrelatedOutboxId = Guid.NewGuid();
            const string deviceId = "purge-contract-device";
            const long purgeRevision = 5;
            const long pullRevision = 8;
            var staleUpdatedAtUtc = new DateTime(
                2026,
                7,
                31,
                4,
                55,
                0,
                DateTimeKind.Utc);
            var purgeUpdatedAtUtc = staleUpdatedAtUtc.AddMinutes(5);
            var exactMutationId =
                $"{deviceId}:{nameof(LocalRentalBillingProfile)}:" +
                $"{profileId:N}:{purgeRevision}:" +
                $"{staleUpdatedAtUtc.Ticks}:0";
            const string unrelatedError =
                "unrelated outbox must survive accepted purge convergence";

            db.RentalBillingProfiles.Add(
                new LocalRentalBillingProfile
                {
                    Id = profileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode =
                        OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode =
                        OfficeCodeCatalog.Usenet,
                    ProfileKey =
                        $"PURGED-PROFILE-{profileId:N}",
                    CustomerName =
                        "stale offline rental customer",
                    BusinessNumber = "111-22-33333",
                    ItemName = "stale offline rental item",
                    BillingType = "개별",
                    BillingAdvanceMode = "후불",
                    BillingMethod = "전자세금계산서",
                    BillingDay = 25,
                    BillingCycleMonths = 1,
                    MonthlyAmount = 55_000m,
                    Notes = "must converge to server purge",
                    IsActive = true,
                    Revision = purgeRevision,
                    IsDirty = true,
                    IsDeleted = false,
                    CreatedAtUtc =
                        staleUpdatedAtUtc.AddDays(-10),
                    UpdatedAtUtc = staleUpdatedAtUtc
                });
            db.Units.Add(
                new LocalUnit
                {
                    Id = unrelatedUnitId,
                    Name = "unrelated preserved unit",
                    IsActive = true,
                    Revision = 3,
                    IsDirty = false,
                    CreatedAtUtc =
                        staleUpdatedAtUtc.AddDays(-20),
                    UpdatedAtUtc =
                        staleUpdatedAtUtc.AddDays(-1)
                });
            db.SyncOutboxEntries.AddRange(
                new LocalSyncOutboxEntry
                {
                    Id = exactOutboxId,
                    MutationId = exactMutationId,
                    DeviceId = deviceId,
                    EntityName =
                        nameof(LocalRentalBillingProfile),
                    EntityId = profileId,
                    ExpectedRevision = purgeRevision,
                    TenantCode =
                        TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode =
                        OfficeCodeCatalog.Usenet,
                    BusinessDatabaseName = "USENET",
                    SessionId = session.SessionId,
                    UserId = session.User!.UserId,
                    Status = "Sent",
                    PreparedAtUtc =
                        staleUpdatedAtUtc.AddMinutes(1),
                    SentAtUtc =
                        staleUpdatedAtUtc.AddMinutes(2)
                },
                new LocalSyncOutboxEntry
                {
                    Id = unrelatedOutboxId,
                    MutationId =
                        $"unrelated-unit:{unrelatedUnitId:N}",
                    DeviceId = deviceId,
                    EntityName = nameof(LocalUnit),
                    EntityId = unrelatedUnitId,
                    ExpectedRevision = 3,
                    TenantCode =
                        TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode =
                        OfficeCodeCatalog.Usenet,
                    BusinessDatabaseName = "USENET",
                    SessionId = session.SessionId,
                    UserId = session.User!.UserId,
                    Status = "Failed",
                    ErrorMessage = unrelatedError,
                    PreparedAtUtc =
                        staleUpdatedAtUtc.AddDays(-1)
                });
            db.Settings.AddRange(
                new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = receiptExistsBeforePush
                        ? purgeRevision.ToString()
                        : (purgeRevision - 1).ToString()
                },
                new LocalSetting
                {
                    Key = "Sync.DeviceId",
                    Value = deviceId
                });
            if (receiptExistsBeforePush)
            {
                db.DeferredRecycleBinPurgeRecords.Add(
                    CreateDeferredPurgeRecord(
                        receiptId,
                        "rental-billing-profile",
                        profileId,
                        purgeRevision,
                        "USENET",
                        purgeUpdatedAtUtc));
            }

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var pullResponse = new SyncPullResponse
            {
                CurrentServerRevision = pullRevision,
                PurgeRecords = receiptExistsBeforePush
                    ? []
                    :
                    [
                        new RecycleBinPurgeRecordDto
                        {
                            Id = receiptId,
                            Kind =
                                "rental-billing-profile",
                            EntityId = profileId,
                            TenantCode =
                                TenantScopeCatalog
                                    .UsenetGroup,
                            OfficeCode =
                                OfficeCodeCatalog.Usenet,
                            Revision = purgeRevision,
                            PurgedAtUtc =
                                purgeUpdatedAtUtc,
                            CreatedAtUtc =
                                purgeUpdatedAtUtc,
                            UpdatedAtUtc =
                                purgeUpdatedAtUtc
                        }
                    ]
            };
            async Task AssertPushSideEffectsBeforePullAsync(
                CancellationToken ct)
            {
                await using var pullBoundaryDb =
                    new LocalDbContext();
                var boundaryOutbox = await pullBoundaryDb
                    .SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(current =>
                        current.Id == exactOutboxId,
                        ct);
                Assert.Equal(
                    "Acknowledged",
                    boundaryOutbox.Status);
                Assert.Equal(
                    purgeRevision,
                    boundaryOutbox.AcceptedRevision);

                var profileExists = await pullBoundaryDb
                    .RentalBillingProfiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .AnyAsync(current =>
                        current.Id == profileId,
                        ct);
                var receiptExists = await pullBoundaryDb
                    .DeferredRecycleBinPurgeRecords
                    .AsNoTracking()
                    .AnyAsync(current =>
                        current.Id == receiptId,
                        ct);
                Assert.False(receiptExists);
                if (receiptExistsBeforePush)
                {
                    Assert.False(profileExists);
                    return;
                }

                Assert.True(profileExists);
                var acceptedProfile = await pullBoundaryDb
                    .RentalBillingProfiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(current =>
                        current.Id == profileId,
                        ct);
                Assert.Equal(
                    purgeRevision,
                    acceptedProfile.Revision);
                Assert.False(acceptedProfile.IsDirty);
            }

            var handler =
                new DelayedPushAckThenEmptyPullHandler(
                    profileId,
                    entityName: "RentalBillingProfile",
                    acceptedRevision: purgeRevision,
                    acceptedUpdatedAtUtc:
                        purgeUpdatedAtUtc,
                    pullResponse: pullResponse,
                    beforePullResponseAsync:
                        AssertPushSideEffectsBeforePullAsync);
            using var sync =
                CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            var pushedRequest =
                await handler.PushReceived.Task.WaitAsync(
                    TimeSpan.FromSeconds(15));
            try
            {
                Assert.Equal(deviceId, pushedRequest.DeviceId);
                var pushedProfile = Assert.Single(
                    pushedRequest.RentalBillingProfiles,
                    current => current.Id == profileId);
                Assert.Equal(
                    purgeRevision,
                    pushedProfile.Revision);
                Assert.Equal(
                    purgeRevision,
                    pushedProfile.ExpectedRevision);
                Assert.Equal(
                    staleUpdatedAtUtc,
                    pushedProfile.UpdatedAtUtc);
                Assert.Equal(
                    staleUpdatedAtUtc,
                    pushedProfile.MutationCreatedAtUtc);
                Assert.Equal(
                    exactMutationId,
                    pushedProfile.MutationId);
                Assert.False(pushedProfile.IsDeleted);
                Assert.DoesNotContain(
                    pushedRequest.Units,
                    current => current.Id == unrelatedUnitId);
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.True(
                await syncTask.WaitAsync(
                    TimeSpan.FromSeconds(15)));
            Assert.Equal(1, handler.PushCount);
            Assert.Equal(1, handler.PullCount);

            await using var verificationDb =
                new LocalDbContext();
            Assert.False(await verificationDb
                .RentalBillingProfiles
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == profileId));
            Assert.False(await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .AnyAsync(current =>
                    current.Id == receiptId));

            var acknowledgedOutbox =
                await verificationDb.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(current =>
                        current.Id == exactOutboxId);
            Assert.Equal(
                exactMutationId,
                acknowledgedOutbox.MutationId);
            Assert.Equal(
                "Acknowledged",
                acknowledgedOutbox.Status);
            Assert.NotNull(
                acknowledgedOutbox.AcknowledgedAtUtc);
            // Equality is intentional: only a strictly newer local revision
            // may supersede this purge receipt instead of applying it.
            Assert.Equal(
                purgeRevision,
                acknowledgedOutbox.ExpectedRevision);
            Assert.Equal(
                purgeRevision,
                acknowledgedOutbox.AcceptedRevision);
            Assert.Equal(
                purgeUpdatedAtUtc,
                acknowledgedOutbox.AcceptedUpdatedAtUtc);

            var unrelatedUnit =
                await verificationDb.Units
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(current =>
                        current.Id == unrelatedUnitId);
            Assert.Equal(
                "unrelated preserved unit",
                unrelatedUnit.Name);
            Assert.Equal(3, unrelatedUnit.Revision);
            Assert.False(unrelatedUnit.IsDirty);
            var unrelatedOutbox =
                await verificationDb.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(current =>
                        current.Id == unrelatedOutboxId);
            Assert.Equal("Failed", unrelatedOutbox.Status);
            Assert.Equal(
                unrelatedError,
                unrelatedOutbox.ErrorMessage);
            Assert.Equal(0, unrelatedOutbox.AcceptedRevision);
            Assert.Null(unrelatedOutbox.AcknowledgedAtUtc);
            Assert.Equal(
                pullRevision.ToString(),
                await verificationDb.Settings
                    .AsNoTracking()
                    .Where(current =>
                        current.Key == "LastSyncRevision")
                    .Select(current => current.Value)
                    .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalBillingProfileAcceptedAlias_IsAcknowledgedAndCanonicalizedByPull()
    {
        PrepareAppRoot("georaeplan-rental-profile-accepted-alias");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var profileKey = "USENET|ACCEPTED-ALIAS-PC|개별|후불|25|1|전자세금계산서";
            var localTemporaryProfileId = Guid.NewGuid();
            var canonicalProfileId = Guid.NewGuid();
            var mutationId = $"test-device:{nameof(LocalRentalBillingProfile)}:{localTemporaryProfileId:N}:accepted-alias";
            var acceptedUpdatedAtUtc = DateTime.UtcNow;

            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = localTemporaryProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = profileKey,
                CustomerName = "accepted alias pc customer",
                BusinessNumber = "111-22-33333",
                ItemName = "IMC2010",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingMethod = "전자세금계산서",
                BillingDay = 25,
                BillingCycleMonths = 1,
                MonthlyAmount = 55_000m,
                Revision = 0,
                IsDirty = true,
                CreatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-30),
                UpdatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-20)
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = mutationId,
                DeviceId = "test-device",
                EntityName = nameof(LocalRentalBillingProfile),
                EntityId = localTemporaryProfileId,
                ExpectedRevision = 0,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BusinessDatabaseName = "USENET",
                SessionId = session.SessionId,
                UserId = session.User!.UserId,
                Status = "Sent",
                PreparedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-10),
                SentAtUtc = acceptedUpdatedAtUtc.AddMinutes(-5)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var acceptedRevisions = new List<SyncAcceptedRevisionDto>
            {
                new()
                {
                    EntityName = "RentalBillingProfile",
                    EntityId = canonicalProfileId,
                    Revision = 42,
                    UpdatedAtUtc = acceptedUpdatedAtUtc
                },
                new()
                {
                    EntityName = "RentalBillingProfile",
                    EntityId = localTemporaryProfileId,
                    Revision = 42,
                    UpdatedAtUtc = acceptedUpdatedAtUtc
                }
            };

            var preparedProfile = LocalMappings.ToDto(
                await db.RentalBillingProfiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(profile => profile.Id == localTemporaryProfileId));
            preparedProfile.ExpectedRevision = preparedProfile.Revision;
            preparedProfile.MutationCreatedAtUtc = preparedProfile.UpdatedAtUtc;
            preparedProfile.MutationId = mutationId;
            var preparedRequest = new SyncPushRequest
            {
                DeviceId = "test-device",
                RentalBillingProfiles =
                {
                    preparedProfile
                }
            };

            await InvokeApplyAcceptedRevisionsAsync(sync, acceptedRevisions, preparedRequest);
            await InvokeMarkOutboxAcknowledgedAsync(
                sync,
                preparedRequest,
                acceptedRevisions);

            var acknowledgedOutbox = await db.SyncOutboxEntries.AsNoTracking().SingleAsync();
            Assert.Equal("Acknowledged", acknowledgedOutbox.Status);
            Assert.NotNull(acknowledgedOutbox.AcknowledgedAtUtc);
            Assert.Equal(42, acknowledgedOutbox.AcceptedRevision);
            Assert.Equal(acceptedUpdatedAtUtc, acknowledgedOutbox.AcceptedUpdatedAtUtc);

            var acceptedLocalProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(profile => profile.Id == localTemporaryProfileId);
            Assert.False(acceptedLocalProfile.IsDirty);
            Assert.Equal(42, acceptedLocalProfile.Revision);

            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    RentalBillingProfiles =
                    [
                        new RentalBillingProfileDto
                        {
                            Id = canonicalProfileId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                            ProfileKey = profileKey,
                            CustomerName = "accepted alias pc customer",
                            BusinessNumber = "111-22-33333",
                            ItemName = "IMC2010",
                            BillingType = "개별",
                            BillingAdvanceMode = "후불",
                            BillingMethod = "전자세금계산서",
                            BillingDay = 28,
                            BillingCycleMonths = 1,
                            MonthlyAmount = 77_000m,
                            Revision = 43,
                            CreatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-1),
                            UpdatedAtUtc = acceptedUpdatedAtUtc
                        }
                    ]
                });

            db.ChangeTracker.Clear();
            var profiles = await db.RentalBillingProfiles.IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync();
            var canonicalProfile = Assert.Single(profiles);
            Assert.Equal(canonicalProfileId, canonicalProfile.Id);
            Assert.Equal(profileKey, canonicalProfile.ProfileKey);
            Assert.False(canonicalProfile.IsDirty);
            Assert.Equal(43, canonicalProfile.Revision);
            Assert.Equal(28, canonicalProfile.BillingDay);
            Assert.Equal(77_000m, canonicalProfile.MonthlyAmount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UpsertPulledRentalManagementCompanies_PendingStaleOutboxBlocksCanonicalizationWithoutAcknowledgement()
    {
        PrepareAppRoot("georaeplan-rental-company-canonical-outbox");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var staleCompanyId = Guid.NewGuid();
            var canonicalCompanyId = Guid.NewGuid();
            var unrelatedCompanyId = Guid.NewGuid();
            var staleOutboxId = Guid.NewGuid();
            var unrelatedOutboxId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            db.RentalManagementCompanies.AddRange(
                new LocalRentalManagementCompany
                {
                    Id = staleCompanyId,
                    Code = OfficeCodeCatalog.Itworld,
                    Name = "stale local company",
                    IsActive = true,
                    Revision = 0,
                    IsDirty = false,
                    CreatedAtUtc = now.AddHours(-2),
                    UpdatedAtUtc = now.AddMinutes(-30)
                },
                new LocalRentalManagementCompany
                {
                    Id = unrelatedCompanyId,
                    Code = "OTHER",
                    Name = "unrelated company",
                    IsActive = true,
                    Revision = 3,
                    IsDirty = false,
                    CreatedAtUtc = now.AddHours(-3),
                    UpdatedAtUtc = now.AddMinutes(-20)
                });
            db.SyncOutboxEntries.AddRange(
                new LocalSyncOutboxEntry
                {
                    Id = staleOutboxId,
                    MutationId =
                        $"test-device:{nameof(LocalRentalManagementCompany)}:" +
                        $"{staleCompanyId:N}:0:{now.AddMinutes(-30).Ticks}:0",
                    DeviceId = "test-device",
                    EntityName = nameof(LocalRentalManagementCompany),
                    EntityId = staleCompanyId,
                    ExpectedRevision = 0,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    Status = "Sent",
                    PreparedAtUtc = now.AddMinutes(-25),
                    SentAtUtc = now.AddMinutes(-24)
                },
                new LocalSyncOutboxEntry
                {
                    Id = unrelatedOutboxId,
                    MutationId =
                        $"test-device:{nameof(LocalRentalManagementCompany)}:" +
                        $"{unrelatedCompanyId:N}:3:{now.AddMinutes(-20).Ticks}:0",
                    DeviceId = "test-device",
                    EntityName = nameof(LocalRentalManagementCompany),
                    EntityId = unrelatedCompanyId,
                    ExpectedRevision = 3,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    Status = "Sent",
                    PreparedAtUtc = now.AddMinutes(-15),
                    SentAtUtc = now.AddMinutes(-14)
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeUpsertPulledRentalManagementCompaniesAsync(
                    sync,
                    [
                        new RentalManagementCompanyDto
                        {
                            Id = canonicalCompanyId,
                            TenantCode = TenantScopeCatalog.Itworld,
                            Code = OfficeCodeCatalog.Itworld,
                            Name = "canonical server company",
                            IsActive = true,
                            Revision = 7,
                            CreatedAtUtc = now.AddHours(-4),
                            UpdatedAtUtc = now
                        }
                    ]));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);

            db.ChangeTracker.Clear();
            var companies = await db.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .OrderBy(company => company.Code)
                .ToListAsync();
            Assert.Equal(2, companies.Count);
            Assert.Contains(
                companies,
                company =>
                    company.Id == staleCompanyId &&
                    company.Name == "stale local company" &&
                    !company.IsDirty);
            Assert.DoesNotContain(
                companies,
                company => company.Id == canonicalCompanyId);
            Assert.Contains(
                companies,
                company => company.Id == unrelatedCompanyId);

            var staleOutbox = await db.SyncOutboxEntries
                .AsNoTracking()
                .SingleAsync(entry => entry.Id == staleOutboxId);
            Assert.Equal("Sent", staleOutbox.Status);
            Assert.Null(staleOutbox.AcknowledgedAtUtc);
            Assert.True(string.IsNullOrEmpty(staleOutbox.ErrorMessage));

            var unrelatedOutbox = await db.SyncOutboxEntries
                .AsNoTracking()
                .SingleAsync(entry => entry.Id == unrelatedOutboxId);
            Assert.Equal("Sent", unrelatedOutbox.Status);
            Assert.Null(unrelatedOutbox.AcknowledgedAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("Prepared", "current")]
    [InlineData("Sent", "current")]
    [InlineData("Failed", "current")]
    [InlineData("Prepared", "legacy-alias")]
    [InlineData("Prepared", "incomplete-legacy")]
    [InlineData("Prepared", "other-business-database")]
    [InlineData("Prepared", "other-tenant")]
    [InlineData("Prepared", "other-office")]
    [InlineData("Prepared", "other-responsible-office")]
    [InlineData("Prepared", "other-device")]
    [InlineData("Prepared", "other-session")]
    [InlineData("Prepared", "other-user")]
    [InlineData("Prepared", "newer-expected-revision")]
    public async Task UpsertPulledRentalManagementCompanies_AnyPendingStaleOutboxFailsClosedRegardlessOfMetadata(
        string status,
        string variant)
    {
        PrepareAppRoot($"georaeplan-rental-company-pending-{status}-{variant}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var staleCompanyId = Guid.NewGuid();
            var canonicalCompanyId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.RentalManagementCompanies.Add(new LocalRentalManagementCompany
            {
                Id = staleCompanyId,
                Code = OfficeCodeCatalog.Itworld,
                Name = "pending stale company",
                IsActive = true,
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddMinutes(-30)
            });

            var outbox = new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = $"test-device:{nameof(LocalRentalManagementCompany)}:{staleCompanyId:N}:3",
                DeviceId = "test-device",
                EntityName = nameof(LocalRentalManagementCompany),
                EntityId = staleCompanyId,
                ExpectedRevision = 3,
                TenantCode = session.TenantCode,
                OfficeCode = session.OfficeCode,
                ResponsibleOfficeCode = session.OfficeCode,
                BusinessDatabaseName = session.SelectedBusinessDatabaseName,
                SessionId = session.SessionId,
                UserId = session.User!.UserId,
                Status = status,
                ErrorMessage = status == "Failed" ? "preserve failure" : string.Empty,
                PreparedAtUtc = now.AddMinutes(-25),
                SentAtUtc = status == "Prepared" ? null : now.AddMinutes(-24)
            };
            switch (variant)
            {
                case "legacy-alias":
                    outbox.EntityName = "RentalManagementCompany";
                    break;
                case "incomplete-legacy":
                    outbox.EntityName = "RentalManagementCompany";
                    outbox.MutationId = string.Empty;
                    outbox.DeviceId = string.Empty;
                    outbox.TenantCode = string.Empty;
                    outbox.OfficeCode = string.Empty;
                    outbox.ResponsibleOfficeCode = string.Empty;
                    outbox.BusinessDatabaseName = string.Empty;
                    outbox.SessionId = Guid.Empty;
                    outbox.UserId = Guid.Empty;
                    break;
                case "other-business-database":
                    outbox.BusinessDatabaseName = "other-business-db";
                    break;
                case "other-tenant":
                    outbox.TenantCode = "OTHER-TENANT";
                    break;
                case "other-office":
                    outbox.OfficeCode = "OTHER-OFFICE";
                    break;
                case "other-responsible-office":
                    outbox.ResponsibleOfficeCode = "OTHER-RESPONSIBLE";
                    break;
                case "other-device":
                    outbox.DeviceId = "other-device";
                    break;
                case "other-session":
                    outbox.SessionId = Guid.NewGuid();
                    break;
                case "other-user":
                    outbox.UserId = Guid.NewGuid();
                    break;
                case "newer-expected-revision":
                    outbox.ExpectedRevision = 99;
                    break;
            }
            db.SyncOutboxEntries.Add(outbox);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var originalMutationId = outbox.MutationId;
            var originalDeviceId = outbox.DeviceId;
            var originalEntityName = outbox.EntityName;
            var originalExpectedRevision = outbox.ExpectedRevision;
            var originalTenantCode = outbox.TenantCode;
            var originalOfficeCode = outbox.OfficeCode;
            var originalResponsibleOfficeCode = outbox.ResponsibleOfficeCode;
            var originalBusinessDatabaseName = outbox.BusinessDatabaseName;
            var originalSessionId = outbox.SessionId;
            var originalUserId = outbox.UserId;
            var originalErrorMessage = outbox.ErrorMessage;
            var originalSentAtUtc = outbox.SentAtUtc;

            using var sync = CreateSyncService(db, session);
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeUpsertPulledRentalManagementCompaniesAsync(
                    sync,
                    [CreatePulledRentalManagementCompany(
                        canonicalCompanyId,
                        OfficeCodeCatalog.Itworld,
                        "canonical server company",
                        revision: 7,
                        now)]));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);

            db.ChangeTracker.Clear();
            var preservedCompany = await db.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(company => company.Id == staleCompanyId);
            Assert.Equal("pending stale company", preservedCompany.Name);
            Assert.Equal(3, preservedCompany.Revision);
            Assert.False(preservedCompany.IsDirty);
            Assert.False(await db.RentalManagementCompanies.IgnoreQueryFilters()
                .AnyAsync(company => company.Id == canonicalCompanyId));

            var preservedOutbox = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry => entry.Id == outbox.Id);
            Assert.Equal(status, preservedOutbox.Status);
            Assert.Null(preservedOutbox.AcknowledgedAtUtc);
            Assert.Equal(originalMutationId, preservedOutbox.MutationId);
            Assert.Equal(originalDeviceId, preservedOutbox.DeviceId);
            Assert.Equal(originalEntityName, preservedOutbox.EntityName);
            Assert.Equal(originalExpectedRevision, preservedOutbox.ExpectedRevision);
            Assert.Equal(originalTenantCode, preservedOutbox.TenantCode);
            Assert.Equal(originalOfficeCode, preservedOutbox.OfficeCode);
            Assert.Equal(originalResponsibleOfficeCode, preservedOutbox.ResponsibleOfficeCode);
            Assert.Equal(originalBusinessDatabaseName, preservedOutbox.BusinessDatabaseName);
            Assert.Equal(originalSessionId, preservedOutbox.SessionId);
            Assert.Equal(originalUserId, preservedOutbox.UserId);
            Assert.Equal(originalErrorMessage, preservedOutbox.ErrorMessage);
            Assert.Equal(originalSentAtUtc, preservedOutbox.SentAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UpsertPulledRentalManagementCompanies_CleanConflictWithoutPendingCompanyOutboxCanonicalizesNormally()
    {
        PrepareAppRoot("georaeplan-rental-company-clean-canonical");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var staleCompanyId = Guid.NewGuid();
            var canonicalCompanyId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.RentalManagementCompanies.Add(new LocalRentalManagementCompany
            {
                Id = staleCompanyId,
                Code = OfficeCodeCatalog.Itworld,
                Name = "clean stale company",
                IsActive = true,
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddMinutes(-30)
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = "unrelated-entity-same-id",
                EntityName = nameof(LocalCustomer),
                EntityId = staleCompanyId,
                Status = "Sent",
                PreparedAtUtc = now.AddMinutes(-20)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            await InvokeUpsertPulledRentalManagementCompaniesAsync(
                sync,
                [CreatePulledRentalManagementCompany(
                    canonicalCompanyId,
                    OfficeCodeCatalog.Itworld,
                    "canonical server company",
                    revision: 7,
                    now)]);

            db.ChangeTracker.Clear();
            var company = Assert.Single(await db.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync());
            Assert.Equal(canonicalCompanyId, company.Id);
            Assert.Equal("canonical server company", company.Name);
            Assert.False(company.IsDirty);
            Assert.Equal("Sent", await db.SyncOutboxEntries.AsNoTracking()
                .Select(entry => entry.Status)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UpsertPulledRentalManagementCompanies_ExactTombstoneWithPendingLegacyOutboxFailsClosed()
    {
        PrepareAppRoot("georaeplan-rental-company-tombstone-pending");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var companyId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.RentalManagementCompanies.Add(new LocalRentalManagementCompany
            {
                Id = companyId,
                Code = OfficeCodeCatalog.Itworld,
                Name = "active local company",
                IsActive = true,
                IsDeleted = false,
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddMinutes(-30)
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = "legacy-tombstone-pending",
                EntityName = "RentalManagementCompany",
                EntityId = companyId,
                Status = "Failed",
                ExpectedRevision = 9,
                PreparedAtUtc = now.AddMinutes(-20),
                ErrorMessage = "preserve me"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeUpsertPulledRentalManagementCompaniesAsync(
                    sync,
                    [CreatePulledRentalManagementCompany(
                        companyId,
                        OfficeCodeCatalog.Itworld,
                        "server tombstone",
                        revision: 8,
                        now,
                        isDeleted: true)]));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);

            db.ChangeTracker.Clear();
            var preserved = await db.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(company => company.Id == companyId);
            Assert.False(preserved.IsDeleted);
            Assert.Equal("active local company", preserved.Name);
            Assert.Equal(4, preserved.Revision);
            var outbox = await db.SyncOutboxEntries.AsNoTracking().SingleAsync();
            Assert.Equal("Failed", outbox.Status);
            Assert.Null(outbox.AcknowledgedAtUtc);
            Assert.Equal("preserve me", outbox.ErrorMessage);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UpsertPulledRentalManagementCompanies_ExactTombstoneWithoutPendingOutboxPreservesCleanTombstone()
    {
        PrepareAppRoot("georaeplan-rental-company-tombstone-clean");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var companyId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.RentalManagementCompanies.Add(new LocalRentalManagementCompany
            {
                Id = companyId,
                Code = OfficeCodeCatalog.Itworld,
                Name = "active local company",
                IsActive = true,
                IsDeleted = false,
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddMinutes(-30)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            await InvokeUpsertPulledRentalManagementCompaniesAsync(
                sync,
                [CreatePulledRentalManagementCompany(
                    companyId,
                    OfficeCodeCatalog.Itworld,
                    "server tombstone",
                    revision: 8,
                    now,
                    isDeleted: true)]);

            db.ChangeTracker.Clear();
            var tombstone = await db.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(company => company.Id == companyId);
            Assert.True(tombstone.IsDeleted);
            Assert.False(tombstone.IsActive);
            Assert.False(tombstone.IsDirty);
            Assert.Equal("server tombstone", tombstone.Name);
            Assert.Equal(8, tombstone.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyPull_RentalManagementCompanyPendingOutboxRollsBackEarlierEntitiesAndCursor()
    {
        PrepareAppRoot("georaeplan-rental-company-pending-pull-rollback");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var staleCompanyId = Guid.NewGuid();
            var canonicalCompanyId = Guid.NewGuid();
            var incomingUnitId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "5"
            });
            db.RentalManagementCompanies.Add(new LocalRentalManagementCompany
            {
                Id = staleCompanyId,
                Code = OfficeCodeCatalog.Itworld,
                Name = "stale before atomic pull",
                IsActive = true,
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddMinutes(-30)
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = "atomic-pull-stale-company",
                EntityName = nameof(LocalRentalManagementCompany),
                EntityId = staleCompanyId,
                Status = "Sent",
                ExpectedRevision = 3,
                PreparedAtUtc = now.AddMinutes(-20),
                SentAtUtc = now.AddMinutes(-19)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeApplyPullAndUpdateRevisionAsync(
                    sync,
                    new SyncPullResponse
                    {
                        CurrentServerRevision = 9,
                        Units =
                        [
                            new UnitDto
                            {
                                Id = incomingUnitId,
                                Name = "must roll back",
                                IsActive = true,
                                Revision = 9,
                                CreatedAtUtc = now.AddHours(-1),
                                UpdatedAtUtc = now
                            }
                        ],
                        RentalManagementCompanies =
                        [
                            CreatePulledRentalManagementCompany(
                                canonicalCompanyId,
                                OfficeCodeCatalog.Itworld,
                                "canonical server company",
                                revision: 9,
                                now)
                        ]
                    },
                    sinceRevision: 5));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);

            await using var verificationDb = new LocalDbContext();
            Assert.False(await verificationDb.Units.IgnoreQueryFilters()
                .AnyAsync(unit => unit.Id == incomingUnitId));
            Assert.Equal("5", await verificationDb.Settings.AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value)
                .SingleAsync());
            Assert.True(await verificationDb.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AnyAsync(company =>
                    company.Id == staleCompanyId &&
                    company.Name == "stale before atomic pull"));
            Assert.False(await verificationDb.RentalManagementCompanies
                .IgnoreQueryFilters()
                .AnyAsync(company => company.Id == canonicalCompanyId));
            var outbox = await verificationDb.SyncOutboxEntries
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal("Sent", outbox.Status);
            Assert.Null(outbox.AcknowledgedAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AuthoritativePullOnly_CompleteStockSnapshotWithDirtyItemFailsClosed()
    {
        PrepareAppRoot("georaeplan-authoritative-stock-dirty-item");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var dirtyItemId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "5" });
            db.Items.Add(new LocalItem
            {
                Id = dirtyItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "dirty stock owner item",
                NameMatchKey = "Dirtystockowneritem",
                Revision = 4,
                IsDirty = true,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new AuthoritativePullOnlyHandler(new SyncPullResponse
            {
                CurrentServerRevision = 9
            });
            using var sync = CreateSyncService(db, session, handler);
            db.ChangeTracker.Clear();

            Assert.False(await sync.TryAuthoritativePullOnlyAsync());
            Assert.Equal(1, handler.PullCount);
            Assert.Equal(0, handler.PushCount);
            Assert.Equal("5", await db.Settings.AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value)
                .SingleAsync());
            var preserved = await db.Items.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(item => item.Id == dirtyItemId);
            Assert.True(preserved.IsDirty);
            Assert.Equal("dirty stock owner item", preserved.NameOriginal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AuthoritativePullOnly_PulledInvoiceWithDirtyVersionSiblingFailsClosed()
    {
        PrepareAppRoot("georaeplan-authoritative-invoice-dirty-version-sibling");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            var dirtySiblingId = Guid.NewGuid();
            var incomingInvoiceId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "5" });
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "invoice sibling customer",
                NameMatchKey = "INVOICESIBLINGCUSTOMER",
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddHours(-1)
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = dirtySiblingId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 8, 9),
                VersionGroupId = versionGroupId,
                IsLatestVersion = true,
                Revision = 4,
                IsDirty = true,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new AuthoritativePullOnlyHandler(new SyncPullResponse
            {
                CurrentServerRevision = 9,
                Invoices =
                [
                    new InvoiceDto
                    {
                        Id = incomingInvoiceId,
                        CustomerId = customerId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        VoucherType = VoucherType.Sales,
                        InvoiceDate = new DateOnly(2026, 8, 9),
                        VersionGroupId = versionGroupId,
                        IsLatestVersion = true,
                        Revision = 9,
                        CreatedAtUtc = now.AddHours(-1),
                        UpdatedAtUtc = now
                    }
                ]
            });
            using var sync = CreateSyncService(db, session, handler);
            db.ChangeTracker.Clear();

            Assert.False(await sync.TryAuthoritativePullOnlyAsync());
            Assert.Equal(1, handler.PullCount);
            Assert.Equal(0, handler.PushCount);
            Assert.Equal("5", await db.Settings.AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value)
                .SingleAsync());
            Assert.False(await db.Invoices.IgnoreQueryFilters()
                .AnyAsync(invoice => invoice.Id == incomingInvoiceId));
            Assert.True(await db.Invoices.IgnoreQueryFilters()
                .Where(invoice => invoice.Id == dirtySiblingId)
                .Select(invoice => invoice.IsDirty)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AuthoritativePullOnly_ServerRevisionRegressionMarksMirrorRequiredAndKeepsCursor()
    {
        PrepareAppRoot("georaeplan-authoritative-server-revision-regression");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "5" });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new AuthoritativePullOnlyHandler(new SyncPullResponse
            {
                CurrentServerRevision = 4
            });
            using var sync = CreateSyncService(db, session, handler);
            db.ChangeTracker.Clear();

            Assert.False(await sync.TryAuthoritativePullOnlyAsync());
            Assert.Equal(1, handler.PullCount);
            Assert.Equal(0, handler.PushCount);
            Assert.Equal("5", await db.Settings.AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value)
                .SingleAsync());
            var dispatcher = new SyncRequestDispatcher();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                dispatcher,
                session);
            Assert.True(await local.IsServerMirrorRefreshRequiredAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AuthoritativePullOnly_FirstCatalogCapabilityWithPendingCleanItemFailsBeforeInternalDirtyTransition()
    {
        PrepareAppRoot("georaeplan-authoritative-first-catalog-capability");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var itemId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "5" });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "catalog capability pending item",
                NameMatchKey = "catalogcapabilitypendingitem",
                CatalogExtensionSyncPending = true,
                IsDirty = false,
                Revision = 4,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new AuthoritativePullOnlyHandler(new SyncPullResponse
            {
                CurrentServerRevision = 9,
                ItemCatalogExtensionVersion = 1,
                Items =
                [
                    new ItemDto
                    {
                        Id = itemId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        NameOriginal = "server catalog item",
                        NameMatchKey = "servercatalogitem",
                        Revision = 9,
                        CreatedAtUtc = now.AddHours(-1),
                        UpdatedAtUtc = now
                    }
                ]
            });
            using var sync = CreateSyncService(db, CreateAdminSession(), handler);
            db.ChangeTracker.Clear();

            Assert.False(await sync.TryAuthoritativePullOnlyAsync());
            Assert.Equal(1, handler.PullCount);
            Assert.Equal(0, handler.PushCount);
            Assert.Equal("5", await db.Settings.AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value)
                .SingleAsync());
            var preserved = await db.Items.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.False(preserved.IsDirty);
            Assert.True(preserved.CatalogExtensionSyncPending);
            Assert.Equal("catalog capability pending item", preserved.NameOriginal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("incremental")]
    [InlineData("authoritative")]
    [InlineData("mirror")]
    public async Task PullNew_PendingEligibleReconciliationBlocksBeforeAnyApply(
        string mode)
    {
        PrepareAppRoot($"georaeplan-pull-reconciliation-gate-{mode}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var unitId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Settings.AddRange(
                new LocalSetting { Key = "LastSyncRevision", Value = "5" },
                new LocalSetting { Key = "Sync.DeviceId", Value = "test-device" });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "eligible reconciliation item",
                NameMatchKey = "eligiblereconciliationitem",
                Revision = 12,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            });
            var pending = CreateScopedOutbox(
                "eligible-reconciliation",
                itemId,
                "Sent",
                now.AddMinutes(-5),
                session);
            pending.ExpectedRevision = 11;
            db.SyncOutboxEntries.Add(pending);
            await db.SaveChangesAsync();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            if (mode == "mirror")
                await local.MarkServerMirrorRefreshRequiredAsync();
            db.ChangeTracker.Clear();

            var handler = new AuthoritativePullOnlyHandler(new SyncPullResponse
            {
                CurrentServerRevision = 9,
                Units =
                [
                    new UnitDto
                    {
                        Id = unitId,
                        Name = "MUST NOT APPLY",
                        IsActive = true,
                        Revision = 9,
                        CreatedAtUtc = now.AddHours(-1),
                        UpdatedAtUtc = now
                    }
                ]
            });
            using var sync = CreateSyncService(db, session, handler);
            if (mode == "authoritative")
            {
                Assert.False(await InvokePullNewCoreAsync(sync, true));
            }
            else
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                    InvokePullNewCoreAsync(sync, false));
                Assert.Equal("SyncPullBlockedException", error.GetType().Name);
            }

            Assert.Equal(0, handler.PullCount);
            Assert.False(await db.Units.IgnoreQueryFilters()
                .AnyAsync(unit => unit.Id == unitId));
            Assert.Equal("5", await db.Settings.AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value).SingleAsync());
            Assert.Equal("Sent", await db.SyncOutboxEntries.AsNoTracking()
                .Where(entry => entry.Id == pending.Id)
                .Select(entry => entry.Status).SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("acknowledged")]
    [InlineData("other-owner")]
    [InlineData("malformed")]
    [InlineData("dirty")]
    [InlineData("revision-not-newer")]
    public async Task PullNew_IneligibleReconciliationCandidateDoesNotBlock(
        string reason)
    {
        PrepareAppRoot($"georaeplan-pull-reconciliation-nonblock-{reason}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var unitId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Settings.AddRange(
                new LocalSetting { Key = "LastSyncRevision", Value = "5" },
                new LocalSetting { Key = "Sync.DeviceId", Value = "test-device" });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "nonblocking reconciliation item",
                NameMatchKey = "nonblockingreconciliationitem",
                Revision = 12,
                IsDirty = reason == "dirty",
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            });
            var candidate = CreateScopedOutbox(
                "ineligible-reconciliation",
                itemId,
                reason == "acknowledged" ? "Acknowledged" : "Sent",
                now.AddMinutes(-5),
                session,
                userId: reason == "other-owner" ? Guid.NewGuid() : null);
            candidate.ExpectedRevision = reason == "revision-not-newer" ? 12 : 11;
            if (reason == "malformed")
                candidate.MutationId = string.Empty;
            db.SyncOutboxEntries.Add(candidate);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new AuthoritativePullOnlyHandler(new SyncPullResponse
            {
                CurrentServerRevision = 9,
                Units =
                [
                    new UnitDto
                    {
                        Id = unitId,
                        Name = "ALLOWED RECONCILIATION UNIT",
                        IsActive = true,
                        Revision = 9,
                        CreatedAtUtc = now.AddHours(-1),
                        UpdatedAtUtc = now
                    }
                ]
            });
            using var sync = CreateSyncService(db, session, handler);
            Assert.True(await InvokePullNewCoreAsync(sync, false));

            Assert.Equal(1, handler.PullCount);
            Assert.True(await db.Units.IgnoreQueryFilters()
                .AnyAsync(unit => unit.Id == unitId));
            Assert.Equal("9", await db.Settings.AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value).SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PullNew_EligibleReconciliationArrivingDuringHttpBlocksApplyAndCursor(
        bool authoritative)
    {
        PrepareAppRoot($"georaeplan-pull-reconciliation-http-race-{authoritative}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var unitId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Settings.AddRange(
                new LocalSetting { Key = "LastSyncRevision", Value = "5" },
                new LocalSetting { Key = "Sync.DeviceId", Value = "test-device" });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "http race reconciliation item",
                NameMatchKey = "httpracereconciliationitem",
                Revision = 12,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(response: new SyncPullResponse
            {
                CurrentServerRevision = 9,
                Units =
                [
                    new UnitDto
                    {
                        Id = unitId,
                        Name = "HTTP RACE MUST NOT APPLY",
                        IsActive = true,
                        Revision = 9,
                        CreatedAtUtc = now.AddHours(-1),
                        UpdatedAtUtc = now
                    }
                ]
            });
            using var sync = CreateSyncService(db, session, handler);
            var pullTask = InvokePullNewCoreAsync(sync, authoritative);
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using (var otherDb = new LocalDbContext())
            {
                var candidate = CreateScopedOutbox(
                    "http-race-reconciliation",
                    itemId,
                    "Failed",
                    now.AddMinutes(-5),
                    session);
                candidate.ExpectedRevision = 11;
                otherDb.SyncOutboxEntries.Add(candidate);
                await otherDb.SaveChangesAsync();
            }
            handler.ReleasePull();

            if (authoritative)
                Assert.False(await pullTask.WaitAsync(TimeSpan.FromSeconds(5)));
            else
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                    pullTask.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal("SyncPullBlockedException", error.GetType().Name);
            }
            Assert.Equal(1, handler.PullCount);
            Assert.False(await db.Units.IgnoreQueryFilters()
                .AnyAsync(unit => unit.Id == unitId));
            Assert.Equal("5", await db.Settings.AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value).SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task FullMirror_EligibleReconciliationArrivingDuringHttpBlocksResetApplyAndCursor()
    {
        PrepareAppRoot("georaeplan-full-mirror-reconciliation-http-race");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var existingCustomerId = Guid.NewGuid();
            var incomingCustomerId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Settings.AddRange(
                new LocalSetting { Key = "LastSyncRevision", Value = "5" },
                new LocalSetting { Key = "Sync.DeviceId", Value = "test-device" });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "mirror race reconciliation item",
                NameMatchKey = "mirrorracereconciliationitem",
                Revision = 12,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now
            });
            db.Customers.Add(new LocalCustomer
            {
                Id = existingCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "existing mirror customer",
                NameMatchKey = "existingmirrorcustomer",
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddHours(-1)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(response: new SyncPullResponse
            {
                CurrentServerRevision = 9,
                Customers =
                [
                    new CustomerDto
                    {
                        Id = incomingCustomerId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        NameOriginal = "incoming mirror customer",
                        NameMatchKey = "incomingmirrorcustomer",
                        TradeType = CustomerClassificationNormalizer.Sales,
                        Revision = 9,
                        CreatedAtUtc = now.AddHours(-1),
                        UpdatedAtUtc = now
                    }
                ]
            });
            using var sync = CreateSyncService(db, session, handler);
            var refreshTask = InvokeTryRefreshSharedMirrorCoreAsync(sync);
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using (var otherDb = new LocalDbContext())
            {
                var candidate = CreateScopedOutbox(
                    "mirror-http-race-reconciliation",
                    itemId,
                    "Sent",
                    now.AddMinutes(-5),
                    session);
                candidate.ExpectedRevision = 11;
                otherDb.SyncOutboxEntries.Add(candidate);
                await otherDb.SaveChangesAsync();
            }
            handler.ReleasePull();

            Assert.False(await refreshTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, handler.PullCount);
            Assert.True(await db.Customers.IgnoreQueryFilters()
                .AnyAsync(customer => customer.Id == existingCustomerId));
            Assert.False(await db.Customers.IgnoreQueryFilters()
                .AnyAsync(customer => customer.Id == incomingCustomerId));
            Assert.Equal("5", await db.Settings.AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value).SingleAsync());
            Assert.Equal("Sent", await db.SyncOutboxEntries.AsNoTracking()
                .Select(entry => entry.Status).SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("unit", false)]
    [InlineData("unit", true)]
    [InlineData("item", false)]
    [InlineData("item", true)]
    [InlineData("profile", false)]
    [InlineData("profile", true)]
    [InlineData("asset", false)]
    [InlineData("asset", true)]
    public async Task CanonicalPullDelete_PendingLocalOrLegacyOutboxFailsClosedBeforeMutation(
        string root,
        bool legacyEntityName)
    {
        PrepareAppRoot($"georaeplan-canonical-delete-pending-{root}-{legacyEntityName}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var staleId = Guid.NewGuid();
            var incomingId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            Func<SyncService, Task> invoke;
            string localEntityName;
            string legacyName;

            switch (root)
            {
                case "unit":
                    localEntityName = nameof(LocalUnit);
                    legacyName = "Unit";
                    db.Units.Add(new LocalUnit
                    {
                        Id = staleId,
                        Name = "CUSTOM OUTBOX UNIT",
                        IsActive = true,
                        Revision = 3,
                        IsDirty = false,
                        CreatedAtUtc = now.AddHours(-2),
                        UpdatedAtUtc = now.AddMinutes(-30)
                    });
                    invoke = sync => InvokeUpsertPulledUnitsAsync(sync,
                    [
                        new UnitDto
                        {
                            Id = incomingId,
                            Name = "CUSTOM OUTBOX UNIT",
                            IsActive = true,
                            Revision = 8,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now
                        }
                    ]);
                    break;
                case "item":
                    localEntityName = nameof(LocalItem);
                    legacyName = "Item";
                    db.Items.Add(new LocalItem
                    {
                        Id = staleId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        NameOriginal = "pending alias item",
                        NameMatchKey = "pendingaliasitem",
                        SpecificationOriginal = "SPEC-PENDING",
                        SpecificationMatchKey = "spec-pending",
                        MaterialNumber = "MAT-PENDING",
                        SerialNumber = "SER-PENDING",
                        Revision = 3,
                        IsDirty = false,
                        CreatedAtUtc = now.AddHours(-2),
                        UpdatedAtUtc = now.AddMinutes(-30)
                    });
                    invoke = sync => InvokeUpsertPulledItemsAsync(sync,
                        new SyncPullResponse
                        {
                            Items =
                            [
                                new ItemDto
                                {
                                    Id = incomingId,
                                    TenantCode = TenantScopeCatalog.UsenetGroup,
                                    OfficeCode = OfficeCodeCatalog.Usenet,
                                    NameOriginal = "pending alias item",
                                    NameMatchKey = "pendingaliasitem",
                                    SpecificationOriginal = "SPEC-PENDING",
                                    SpecificationMatchKey = "spec-pending",
                                    MaterialNumber = "MAT-PENDING",
                                    SerialNumber = "SER-PENDING",
                                    Revision = 8,
                                    CreatedAtUtc = now.AddHours(-1),
                                    UpdatedAtUtc = now
                                }
                            ]
                        });
                    break;
                case "profile":
                    localEntityName = nameof(LocalRentalBillingProfile);
                    legacyName = "RentalBillingProfile";
                    db.RentalBillingProfiles.Add(CreateTestRentalBillingProfile(
                        staleId,
                        "PENDING-PROFILE-KEY",
                        isDirty: false,
                        now));
                    invoke = sync => InvokeUpsertPulledRentalBillingProfilesAsync(sync,
                    [
                        CreateTestRentalBillingProfileDto(
                            incomingId,
                            "PENDING-PROFILE-KEY",
                            revision: 8,
                            now)
                    ]);
                    break;
                case "asset":
                    localEntityName = nameof(LocalRentalAsset);
                    legacyName = "RentalAsset";
                    db.RentalAssets.Add(CreateTestRentalAsset(
                        staleId,
                        "PENDING-ASSET-KEY",
                        isDirty: false,
                        now));
                    invoke = sync => InvokeUpsertPulledRentalAssetsAsync(sync,
                    [
                        CreateTestRentalAssetDto(
                            incomingId,
                            "PENDING-ASSET-KEY",
                            revision: 8,
                            now)
                    ]);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(root));
            }

            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = $"pending-canonical-delete:{root}:{staleId:N}",
                EntityName = legacyEntityName ? legacyName : localEntityName,
                EntityId = staleId,
                Status = "Failed",
                ExpectedRevision = 99,
                ErrorMessage = "must remain pending",
                PreparedAtUtc = now.AddMinutes(-20)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var error = await Assert.ThrowsAnyAsync<Exception>(() => invoke(sync));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);

            db.ChangeTracker.Clear();
            var (staleExists, incomingExists) = root switch
            {
                "unit" => (
                    await db.Units.IgnoreQueryFilters().AnyAsync(entity => entity.Id == staleId),
                    await db.Units.IgnoreQueryFilters().AnyAsync(entity => entity.Id == incomingId)),
                "item" => (
                    await db.Items.IgnoreQueryFilters().AnyAsync(entity => entity.Id == staleId),
                    await db.Items.IgnoreQueryFilters().AnyAsync(entity => entity.Id == incomingId)),
                "profile" => (
                    await db.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(entity => entity.Id == staleId),
                    await db.RentalBillingProfiles.IgnoreQueryFilters().AnyAsync(entity => entity.Id == incomingId)),
                "asset" => (
                    await db.RentalAssets.IgnoreQueryFilters().AnyAsync(entity => entity.Id == staleId),
                    await db.RentalAssets.IgnoreQueryFilters().AnyAsync(entity => entity.Id == incomingId)),
                _ => throw new ArgumentOutOfRangeException(nameof(root))
            };
            Assert.True(staleExists);
            Assert.False(incomingExists);
            var outbox = await db.SyncOutboxEntries.AsNoTracking().SingleAsync();
            Assert.Equal("Failed", outbox.Status);
            Assert.Null(outbox.AcknowledgedAtUtc);
            Assert.Equal("must remain pending", outbox.ErrorMessage);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("transaction", true)]
    [InlineData("transaction", false)]
    [InlineData("invoice", true)]
    [InlineData("invoice", false)]
    [InlineData("asset-billing-profile", true)]
    [InlineData("asset-billing-profile", false)]
    [InlineData("asset-last-billing-profile", true)]
    [InlineData("asset-last-billing-profile", false)]
    [InlineData("assignment-history", true)]
    [InlineData("assignment-history", false)]
    [InlineData("billing-log", true)]
    [InlineData("billing-log", false)]
    public async Task RentalBillingProfileCanonicalDelete_DependencyFailsClosed(
        string dependency,
        bool dependencyDirty)
    {
        PrepareAppRoot($"georaeplan-profile-dirty-dependency-{dependency}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var staleProfileId = Guid.NewGuid();
            var incomingProfileId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.RentalBillingProfiles.Add(CreateTestRentalBillingProfile(
                staleProfileId,
                "DIRTY-DEPENDENCY-PROFILE",
                isDirty: false,
                now));

            if (dependency is "transaction" or "invoice")
            {
                var customerId = Guid.NewGuid();
                db.Customers.Add(new LocalCustomer
                {
                    Id = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "profile dependency customer",
                    NameMatchKey = "PROFILEDEPENDENCYCUSTOMER",
                    IsDirty = false
                });
                if (dependency == "transaction")
                {
                    db.Transactions.Add(new LocalTransaction
                    {
                        Id = Guid.NewGuid(),
                        CustomerId = customerId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                        TransactionDate = new DateOnly(2026, 8, 9),
                        LinkedRentalBillingProfileId = staleProfileId,
                        IsDirty = dependencyDirty
                    });
                }
                else
                {
                    var invoiceId = Guid.NewGuid();
                    db.Invoices.Add(new LocalInvoice
                    {
                        Id = invoiceId,
                        CustomerId = customerId,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                        VoucherType = VoucherType.Sales,
                        InvoiceDate = new DateOnly(2026, 8, 9),
                        VersionGroupId = invoiceId,
                        IsLatestVersion = true,
                        LinkedRentalBillingProfileId = staleProfileId,
                        IsDirty = dependencyDirty
                    });
                }
            }
            else if (dependency is "asset-billing-profile" or "asset-last-billing-profile")
            {
                var asset = CreateTestRentalAsset(
                    Guid.NewGuid(),
                    $"DIRTY-ASSET-{dependency}",
                    isDirty: dependencyDirty,
                    now);
                if (dependency == "asset-billing-profile")
                    asset.BillingProfileId = staleProfileId;
                else
                    asset.LastBillingProfileId = staleProfileId;
                db.RentalAssets.Add(asset);
            }
            else if (dependency == "assignment-history")
            {
                var asset = CreateTestRentalAsset(
                    Guid.NewGuid(),
                    "DIRTY-HISTORY-ASSET",
                    isDirty: false,
                    now);
                db.RentalAssets.Add(asset);
                db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
                {
                    Id = Guid.NewGuid(),
                    AssetId = asset.Id,
                    BillingProfileId = staleProfileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    IsDirty = dependencyDirty
                });
            }
            else
            {
                db.RentalBillingLogs.Add(new LocalRentalBillingLog
                {
                    Id = Guid.NewGuid(),
                    BillingProfileId = staleProfileId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BillingYearMonth = "2026-08",
                    IsDirty = dependencyDirty
                });
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeUpsertPulledRentalBillingProfilesAsync(sync,
                [
                    CreateTestRentalBillingProfileDto(
                        incomingProfileId,
                        "DIRTY-DEPENDENCY-PROFILE",
                        revision: 8,
                        now)
                ]));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);
            Assert.True(await db.RentalBillingProfiles.IgnoreQueryFilters()
                .AnyAsync(profile => profile.Id == staleProfileId));
            Assert.False(await db.RentalBillingProfiles.IgnoreQueryFilters()
                .AnyAsync(profile => profile.Id == incomingProfileId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RentalAssetCanonicalDelete_AssignmentHistoryFailsClosed(
        bool dependencyDirty)
    {
        PrepareAppRoot("georaeplan-asset-dirty-assignment-history");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var staleAssetId = Guid.NewGuid();
            var incomingAssetId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.RentalAssets.Add(CreateTestRentalAsset(
                staleAssetId,
                "DIRTY-HISTORY-ASSET-KEY",
                isDirty: false,
                now));
            db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
            {
                Id = Guid.NewGuid(),
                AssetId = staleAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                IsDirty = dependencyDirty
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeUpsertPulledRentalAssetsAsync(sync,
                [
                    CreateTestRentalAssetDto(
                        incomingAssetId,
                        "DIRTY-HISTORY-ASSET-KEY",
                        revision: 8,
                        now)
                ]));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);
            Assert.True(await db.RentalAssets.IgnoreQueryFilters()
                .AnyAsync(asset => asset.Id == staleAssetId));
            Assert.False(await db.RentalAssets.IgnoreQueryFilters()
                .AnyAsync(asset => asset.Id == incomingAssetId));
            Assert.Equal(dependencyDirty, await db.RentalAssetAssignmentHistories.IgnoreQueryFilters()
                .Where(history => history.AssetId == staleAssetId)
                .Select(history => history.IsDirty)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RentalAssetCanonicalDelete_CandidateMatchingMultipleIncomingIdsFailsClosed()
    {
        PrepareAppRoot("georaeplan-asset-multiple-natural-key-match");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var staleId = Guid.NewGuid();
            var firstIncomingId = Guid.NewGuid();
            var secondIncomingId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var stale = CreateTestRentalAsset(
                staleId,
                "MULTI-STALE",
                isDirty: false,
                now);
            stale.ManagementNumber = "MNO-MULTI-A";
            stale.ManagementId = "MID-MULTI-B";
            db.RentalAssets.Add(stale);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var first = CreateTestRentalAssetDto(
                firstIncomingId,
                "MULTI-FIRST",
                revision: 8,
                now);
            first.ManagementNumber = stale.ManagementNumber;
            first.ManagementId = "MID-FIRST";
            var second = CreateTestRentalAssetDto(
                secondIncomingId,
                "MULTI-SECOND",
                revision: 9,
                now);
            second.ManagementNumber = "MNO-SECOND";
            second.ManagementId = stale.ManagementId;

            using var sync = CreateSyncService(db, CreateAdminSession());
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeUpsertPulledRentalAssetsAsync(sync, [first, second]));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);
            Assert.True(await db.RentalAssets.IgnoreQueryFilters()
                .AnyAsync(asset => asset.Id == staleId));
            Assert.False(await db.RentalAssets.IgnoreQueryFilters()
                .AnyAsync(asset => asset.Id == firstIncomingId || asset.Id == secondIncomingId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PriceGradeOptionCanonicalTombstone_WithDependentGradeFailsClosed()
    {
        PrepareAppRoot("georaeplan-price-grade-option-dependent-grade");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var staleOptionId = Guid.NewGuid();
            var incomingOptionId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var gradeId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "dependent grade item",
                NameMatchKey = "dependentgradeitem",
                IsDirty = false
            });
            db.PriceGradeOptions.Add(new LocalPriceGradeOption
            {
                Id = staleOptionId,
                Name = "DEPENDENT PRICE GRADE",
                PriceSource = SelectionOptionDefaults.PriceSourceSales,
                IsActive = true,
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddMinutes(-30)
            });
            db.ItemPriceGrades.Add(new LocalItemPriceGrade
            {
                Id = gradeId,
                ItemId = itemId,
                PriceGradeOptionId = staleOptionId,
                PriceGradeName = "DEPENDENT PRICE GRADE",
                UnitPrice = 1000m,
                IsActive = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeApplyPullAsync(sync, new SyncPullResponse
                {
                    PriceGradeOptions =
                    [
                        new PriceGradeOptionDto
                        {
                            Id = incomingOptionId,
                            Name = "DEPENDENT PRICE GRADE",
                            PriceSource = SelectionOptionDefaults.PriceSourceSales,
                            IsActive = true,
                            Revision = 8,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now
                        }
                    ]
                }));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);
            Assert.True(await db.PriceGradeOptions.IgnoreQueryFilters()
                .AnyAsync(option => option.Id == staleOptionId && !option.IsDeleted));
            Assert.False(await db.PriceGradeOptions.IgnoreQueryFilters()
                .AnyAsync(option => option.Id == incomingOptionId));
            Assert.Equal(staleOptionId, await db.ItemPriceGrades.AsNoTracking()
                .Where(grade => grade.Id == gradeId)
                .Select(grade => grade.PriceGradeOptionId)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyPull_ItemAliasPendingOutboxRollsBackEarlierUnitAndCursor()
    {
        PrepareAppRoot("georaeplan-item-alias-pending-atomic-rollback");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var staleItemId = Guid.NewGuid();
            var incomingItemId = Guid.NewGuid();
            var incomingUnitId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "5" });
            db.Items.Add(new LocalItem
            {
                Id = staleItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "atomic pending alias item",
                NameMatchKey = "atomicpendingaliasitem",
                SpecificationOriginal = "ATOMIC-SPEC",
                SpecificationMatchKey = "atomic-spec",
                MaterialNumber = "ATOMIC-MAT",
                SerialNumber = "ATOMIC-SER",
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddMinutes(-30)
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = "atomic-item-alias-pending",
                EntityName = "Item",
                EntityId = staleItemId,
                Status = "Sent",
                ExpectedRevision = 3,
                PreparedAtUtc = now.AddMinutes(-20)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeApplyPullAndUpdateRevisionAsync(
                    sync,
                    new SyncPullResponse
                    {
                        CurrentServerRevision = 9,
                        Units =
                        [
                            new UnitDto
                            {
                                Id = incomingUnitId,
                                Name = "ATOMIC ALIAS UNIT",
                                IsActive = true,
                                Revision = 9,
                                CreatedAtUtc = now.AddHours(-1),
                                UpdatedAtUtc = now
                            }
                        ],
                        Items =
                        [
                            new ItemDto
                            {
                                Id = incomingItemId,
                                TenantCode = TenantScopeCatalog.UsenetGroup,
                                OfficeCode = OfficeCodeCatalog.Usenet,
                                NameOriginal = "atomic pending alias item",
                                NameMatchKey = "atomicpendingaliasitem",
                                SpecificationOriginal = "ATOMIC-SPEC",
                                SpecificationMatchKey = "atomic-spec",
                                MaterialNumber = "ATOMIC-MAT",
                                SerialNumber = "ATOMIC-SER",
                                Revision = 9,
                                CreatedAtUtc = now.AddHours(-1),
                                UpdatedAtUtc = now
                            }
                        ]
                    },
                    sinceRevision: 5));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);

            await using var verificationDb = new LocalDbContext();
            Assert.False(await verificationDb.Units.IgnoreQueryFilters()
                .AnyAsync(unit => unit.Id == incomingUnitId));
            Assert.True(await verificationDb.Items.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == staleItemId));
            Assert.False(await verificationDb.Items.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == incomingItemId));
            Assert.Equal("5", await verificationDb.Settings.AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value)
                .SingleAsync());
            Assert.Equal("Sent", await verificationDb.SyncOutboxEntries
                .AsNoTracking()
                .Select(entry => entry.Status)
                .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyPull_ItemAliasChildPendingOutboxesRollBackAllReferencesEarlierUnitAndCursor()
    {
        PrepareAppRoot("georaeplan-item-alias-child-pending-atomic-rollback");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var staleItemId = Guid.NewGuid();
            var incomingItemId = Guid.NewGuid();
            var incomingUnitId = Guid.NewGuid();
            var priceGradeId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var invoiceLineId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var transferId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var optionId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "5" });
            db.Items.Add(new LocalItem
            {
                Id = staleItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "child pending alias item",
                NameMatchKey = "childpendingaliasitem",
                SpecificationOriginal = "CHILD-SPEC",
                SpecificationMatchKey = "child-spec",
                MaterialNumber = "CHILD-MAT",
                SerialNumber = "CHILD-SER",
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddMinutes(-30)
            });
            db.PriceGradeOptions.Add(new LocalPriceGradeOption
            {
                Id = optionId,
                Name = "CHILD GRADE",
                PriceSource = SelectionOptionDefaults.PriceSourceSales,
                IsActive = true,
                IsDirty = false
            });
            db.ItemPriceGrades.Add(new LocalItemPriceGrade
            {
                Id = priceGradeId,
                ItemId = staleItemId,
                PriceGradeOptionId = optionId,
                PriceGradeName = "CHILD GRADE",
                UnitPrice = 1000m,
                IsActive = true,
                IsDirty = false
            });
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "child alias customer",
                NameMatchKey = "childaliascustomer",
                IsDirty = false
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 8, 9),
                VersionGroupId = invoiceId,
                IsLatestVersion = true,
                IsDirty = false,
                Lines =
                [
                    new LocalInvoiceLine
                    {
                        Id = invoiceLineId,
                        ItemId = staleItemId,
                        ItemNameOriginal = "child pending alias item",
                        Quantity = 1m
                    }
                ]
            });
            db.InvoiceLineSerials.Add(new LocalInvoiceLineSerial
            {
                Id = Guid.NewGuid(),
                InvoiceId = invoiceId,
                InvoiceLineId = invoiceLineId,
                ItemId = staleItemId,
                SerialNumber = "CHILD-SER"
            });
            var asset = CreateTestRentalAsset(assetId, "CHILD-ASSET", false, now);
            asset.ItemId = staleItemId;
            db.RentalAssets.Add(asset);
            var profile = CreateTestRentalBillingProfile(profileId, "CHILD-PROFILE", false, now);
            profile.BillingTemplateJson = JsonSerializer.Serialize(new[]
            {
                new { CatalogItemId = staleItemId }
            });
            db.RentalBillingProfiles.Add(profile);
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "CHILD-TRANSFER",
                IsDirty = false,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = Guid.NewGuid(),
                        ItemId = staleItemId,
                        ItemNameOriginal = "child pending alias item",
                        Quantity = 1m
                    }
                ]
            });

            (string EntityName, Guid EntityId, string Status)[] pending =
            [
                (nameof(LocalItemPriceGrade), priceGradeId, "Prepared"),
                ("Invoice", invoiceId, "Sent"),
                (nameof(LocalRentalAsset), assetId, "Failed"),
                ("RentalBillingProfile", profileId, "Prepared"),
                (nameof(LocalInventoryTransfer), transferId, "Sent")
            ];
            foreach (var (entityName, entityId, status) in pending)
            {
                db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
                {
                    Id = Guid.NewGuid(),
                    MutationId = $"child-alias:{entityName}:{entityId:N}",
                    EntityName = entityName,
                    EntityId = entityId,
                    Status = status,
                    PreparedAtUtc = now.AddMinutes(-20)
                });
            }
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeApplyPullAndUpdateRevisionAsync(
                    sync,
                    new SyncPullResponse
                    {
                        CurrentServerRevision = 9,
                        Units =
                        [
                            new UnitDto
                            {
                                Id = incomingUnitId,
                                Name = "CHILD ALIAS ROLLBACK UNIT",
                                IsActive = true,
                                Revision = 9,
                                CreatedAtUtc = now.AddHours(-1),
                                UpdatedAtUtc = now
                            }
                        ],
                        Items =
                        [
                            new ItemDto
                            {
                                Id = incomingItemId,
                                TenantCode = TenantScopeCatalog.UsenetGroup,
                                OfficeCode = OfficeCodeCatalog.Usenet,
                                NameOriginal = "child pending alias item",
                                NameMatchKey = "childpendingaliasitem",
                                SpecificationOriginal = "CHILD-SPEC",
                                SpecificationMatchKey = "child-spec",
                                MaterialNumber = "CHILD-MAT",
                                SerialNumber = "CHILD-SER",
                                Revision = 9,
                                CreatedAtUtc = now.AddHours(-1),
                                UpdatedAtUtc = now
                            }
                        ]
                    },
                    sinceRevision: 5));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);

            await using var verificationDb = new LocalDbContext();
            Assert.False(await verificationDb.Units.IgnoreQueryFilters()
                .AnyAsync(unit => unit.Id == incomingUnitId));
            Assert.True(await verificationDb.Items.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == staleItemId));
            Assert.False(await verificationDb.Items.IgnoreQueryFilters()
                .AnyAsync(item => item.Id == incomingItemId));
            Assert.Equal(staleItemId, await verificationDb.ItemPriceGrades.AsNoTracking()
                .Where(grade => grade.Id == priceGradeId).Select(grade => grade.ItemId).SingleAsync());
            Assert.Equal(staleItemId, await verificationDb.InvoiceLines.AsNoTracking()
                .Where(line => line.Id == invoiceLineId).Select(line => line.ItemId).SingleAsync());
            Assert.Equal(staleItemId, await verificationDb.RentalAssets.IgnoreQueryFilters().AsNoTracking()
                .Where(current => current.Id == assetId).Select(current => current.ItemId).SingleAsync());
            Assert.Contains(staleItemId.ToString("D"), await verificationDb.RentalBillingProfiles
                .IgnoreQueryFilters().AsNoTracking().Where(current => current.Id == profileId)
                .Select(current => current.BillingTemplateJson).SingleAsync(), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(staleItemId, await verificationDb.InventoryTransferLines.AsNoTracking()
                .Where(line => line.TransferId == transferId).Select(line => line.ItemId).SingleAsync());
            Assert.Equal(["Failed", "Prepared", "Prepared", "Sent", "Sent"],
                await verificationDb.SyncOutboxEntries.AsNoTracking()
                    .OrderBy(entry => entry.Status).Select(entry => entry.Status).ToListAsync());
            Assert.Equal("5", await verificationDb.Settings.AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value).SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("price-grade", false)]
    [InlineData("price-grade", true)]
    [InlineData("trade-type", false)]
    [InlineData("trade-type", true)]
    [InlineData("item-category", false)]
    [InlineData("item-category", true)]
    public async Task ApplyPull_SelectionOptionCanonicalSideEffectWithPendingOutboxFailsClosed(
        string optionRoot,
        bool legacyEntityName)
    {
        PrepareAppRoot($"georaeplan-selection-option-pending-{optionRoot}-{legacyEntityName}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var staleId = Guid.NewGuid();
            var incomingId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var pull = new SyncPullResponse();
            string localName;
            string legacyName;
            switch (optionRoot)
            {
                case "price-grade":
                    localName = nameof(LocalPriceGradeOption);
                    legacyName = "PriceGradeOption";
                    db.PriceGradeOptions.Add(new LocalPriceGradeOption
                    {
                        Id = staleId,
                        Name = "PENDING OPTION",
                        PriceSource = SelectionOptionDefaults.PriceSourceSales,
                        IsActive = true,
                        Revision = 3,
                        IsDirty = false,
                        CreatedAtUtc = now.AddHours(-2),
                        UpdatedAtUtc = now.AddMinutes(-30)
                    });
                    pull.PriceGradeOptions.Add(new PriceGradeOptionDto
                    {
                        Id = incomingId,
                        Name = "PENDING OPTION",
                        PriceSource = SelectionOptionDefaults.PriceSourceSales,
                        IsActive = true,
                        Revision = 8,
                        CreatedAtUtc = now.AddHours(-1),
                        UpdatedAtUtc = now
                    });
                    break;
                case "trade-type":
                    localName = nameof(LocalTradeTypeOption);
                    legacyName = "TradeTypeOption";
                    db.TradeTypeOptions.Add(new LocalTradeTypeOption
                    {
                        Id = staleId,
                        Name = "PENDING OPTION",
                        AllowsSales = true,
                        IsActive = true,
                        Revision = 3,
                        IsDirty = false,
                        CreatedAtUtc = now.AddHours(-2),
                        UpdatedAtUtc = now.AddMinutes(-30)
                    });
                    pull.TradeTypeOptions.Add(new TradeTypeOptionDto
                    {
                        Id = incomingId,
                        Name = "PENDING OPTION",
                        AllowsSales = true,
                        IsActive = true,
                        Revision = 8,
                        CreatedAtUtc = now.AddHours(-1),
                        UpdatedAtUtc = now
                    });
                    break;
                default:
                    localName = nameof(LocalItemCategoryOption);
                    legacyName = "ItemCategoryOption";
                    db.ItemCategoryOptions.Add(new LocalItemCategoryOption
                    {
                        Id = staleId,
                        Name = "PENDING OPTION",
                        IsActive = true,
                        Revision = 3,
                        IsDirty = false,
                        CreatedAtUtc = now.AddHours(-2),
                        UpdatedAtUtc = now.AddMinutes(-30)
                    });
                    pull.ItemCategoryOptions.Add(new ItemCategoryOptionDto
                    {
                        Id = incomingId,
                        Name = "PENDING OPTION",
                        IsActive = true,
                        Revision = 8,
                        CreatedAtUtc = now.AddHours(-1),
                        UpdatedAtUtc = now
                    });
                    break;
            }
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = $"selection-option-pending:{optionRoot}",
                EntityName = legacyEntityName ? legacyName : localName,
                EntityId = staleId,
                Status = "Sent",
                PreparedAtUtc = now.AddMinutes(-20)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeApplyPullAsync(sync, pull));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);

            await using var verificationDb = new LocalDbContext();
            var stalePreserved = optionRoot switch
            {
                "price-grade" => await verificationDb.PriceGradeOptions.IgnoreQueryFilters()
                    .AnyAsync(option => option.Id == staleId && !option.IsDeleted && option.IsActive),
                "trade-type" => await verificationDb.TradeTypeOptions.IgnoreQueryFilters()
                    .AnyAsync(option => option.Id == staleId && !option.IsDeleted && option.IsActive),
                _ => await verificationDb.ItemCategoryOptions.IgnoreQueryFilters()
                    .AnyAsync(option => option.Id == staleId && !option.IsDeleted && option.IsActive)
            };
            Assert.True(stalePreserved);
            Assert.Equal("Sent", await verificationDb.SyncOutboxEntries
                .AsNoTracking().Select(entry => entry.Status).SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyPull_CompanyProfileCanonicalSideEffectWithPendingOutboxRollsBackEarlierUnitAndCursor()
    {
        PrepareAppRoot("georaeplan-company-profile-canonical-pending");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var staleId = Guid.NewGuid();
            var incomingId = Guid.NewGuid();
            var incomingUnitId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.Settings.Add(new LocalSetting { Key = "LastSyncRevision", Value = "5" });
            db.CompanyProfiles.Add(new LocalCompanyProfile
            {
                Id = staleId,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ProfileName = $"{OfficeCodeCatalog.Usenet} 기본",
                IsDefaultForOffice = true,
                IsActive = true,
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddMinutes(-30)
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = "company-profile-side-effect-pending",
                EntityName = "CompanyProfile",
                EntityId = staleId,
                Status = "Failed",
                PreparedAtUtc = now.AddMinutes(-20)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeApplyPullAndUpdateRevisionAsync(
                    sync,
                    new SyncPullResponse
                    {
                        CurrentServerRevision = 9,
                        Units =
                        [
                            new UnitDto
                            {
                                Id = incomingUnitId,
                                Name = "COMPANY PROFILE ROLLBACK UNIT",
                                IsActive = true,
                                Revision = 9,
                                CreatedAtUtc = now.AddHours(-1),
                                UpdatedAtUtc = now
                            }
                        ],
                        CompanyProfiles =
                        [
                            new CompanyProfileDto
                            {
                                Id = incomingId,
                                OfficeCode = OfficeCodeCatalog.Usenet,
                                ProfileName = "new server default",
                                IsDefaultForOffice = true,
                                IsActive = true,
                                Revision = 9,
                                CreatedAtUtc = now.AddHours(-1),
                                UpdatedAtUtc = now
                            }
                        ]
                    },
                    sinceRevision: 5));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);

            await using var verificationDb = new LocalDbContext();
            var stale = await verificationDb.CompanyProfiles.IgnoreQueryFilters()
                .AsNoTracking().SingleAsync(profile => profile.Id == staleId);
            Assert.True(stale.IsDefaultForOffice);
            Assert.True(stale.IsActive);
            Assert.False(stale.IsDeleted);
            Assert.False(await verificationDb.CompanyProfiles.IgnoreQueryFilters()
                .AnyAsync(profile => profile.Id == incomingId));
            Assert.False(await verificationDb.Units.IgnoreQueryFilters()
                .AnyAsync(unit => unit.Id == incomingUnitId));
            Assert.Equal("5", await verificationDb.Settings.AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value).SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyPull_CompanyProfileCanonicalSideEffectNeverClearsDirtyDefault()
    {
        PrepareAppRoot("georaeplan-company-profile-dirty-default-preserved");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var dirtyId = Guid.NewGuid();
            var incomingId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.CompanyProfiles.Add(new LocalCompanyProfile
            {
                Id = dirtyId,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ProfileName = "unsent dirty default",
                IsDefaultForOffice = true,
                IsActive = true,
                Revision = 3,
                IsDirty = true,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddMinutes(-30)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            var error = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeApplyPullAsync(sync, new SyncPullResponse
                {
                    CompanyProfiles =
                    [
                        new CompanyProfileDto
                        {
                            Id = incomingId,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            ProfileName = "server competing default",
                            IsDefaultForOffice = true,
                            IsActive = true,
                            Revision = 9,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now
                        }
                    ]
                }));
            Assert.Equal("SyncPullBlockedException", error.GetType().Name);

            var dirty = await db.CompanyProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(profile => profile.Id == dirtyId);
            Assert.True(dirty.IsDirty);
            Assert.True(dirty.IsDefaultForOffice);
            Assert.True(dirty.IsActive);
            Assert.False(dirty.IsDeleted);
            Assert.False(await db.CompanyProfiles.IgnoreQueryFilters()
                .AnyAsync(profile => profile.Id == incomingId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyPull_CompanyProfileExactSnapshotWithPendingOutboxAndNoCanonicalSideEffectRemainsAllowed()
    {
        PrepareAppRoot("georaeplan-company-profile-exact-pending-positive");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var profileId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            db.CompanyProfiles.Add(new LocalCompanyProfile
            {
                Id = profileId,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ProfileName = "exact local profile",
                IsDefaultForOffice = false,
                IsActive = true,
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-2),
                UpdatedAtUtc = now.AddMinutes(-30)
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = Guid.NewGuid(),
                MutationId = "company-profile-exact-pending",
                EntityName = nameof(LocalCompanyProfile),
                EntityId = profileId,
                Status = "Sent",
                PreparedAtUtc = now.AddMinutes(-20)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            await InvokeApplyPullAsync(sync, new SyncPullResponse
            {
                CompanyProfiles =
                [
                    new CompanyProfileDto
                    {
                        Id = profileId,
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        ProfileName = "exact server tombstone",
                        IsDefaultForOffice = false,
                        IsActive = false,
                        IsDeleted = true,
                        Revision = 9,
                        CreatedAtUtc = now.AddHours(-2),
                        UpdatedAtUtc = now
                    }
                ]
            });

            var stored = await db.CompanyProfiles.IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(profile => profile.Id == profileId);
            Assert.True(stored.IsDeleted);
            Assert.False(stored.IsActive);
            Assert.Equal("exact server tombstone", stored.ProfileName);
            Assert.Equal("Sent", await db.SyncOutboxEntries.AsNoTracking()
                .Select(entry => entry.Status).SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task PaymentTransactionAttachmentAcceptedRevisions_KeepUnacceptedRowsDirtyAndAcknowledgeEachEntity()
    {
        PrepareAppRoot("georaeplan-payment-transaction-attachment-accepted-revisions");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var sharedId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var preparedAtUtc = DateTime.UtcNow.AddMinutes(-5);
            const string paymentMutationId = "test-device:LocalPayment:atomic-accepted";
            const string transactionMutationId = "test-device:LocalTransaction:atomic-accepted";
            const string attachmentMutationId = "test-device:LocalTransactionAttachment:atomic-accepted";

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "accepted revision atomic customer",
                NameMatchKey = "ACCEPTEDREVISIONATOMICCUSTOMER",
                IsDirty = false,
                CreatedAtUtc = preparedAtUtc.AddMinutes(-10),
                UpdatedAtUtc = preparedAtUtc.AddMinutes(-10)
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 8, 3),
                VersionGroupId = invoiceId,
                IsLatestVersion = true,
                IsDirty = false,
                CreatedAtUtc = preparedAtUtc.AddMinutes(-10),
                UpdatedAtUtc = preparedAtUtc.AddMinutes(-10)
            });
            db.Payments.Add(new LocalPayment
            {
                Id = sharedId,
                InvoiceId = invoiceId,
                PaymentDate = new DateOnly(2026, 8, 3),
                Amount = 30_000m,
                Note = "atomic payment",
                Revision = 3,
                IsDirty = true,
                CreatedAtUtc = preparedAtUtc.AddMinutes(-2),
                UpdatedAtUtc = preparedAtUtc
            });
            db.Transactions.Add(new LocalTransaction
            {
                Id = sharedId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionDate = new DateOnly(2026, 8, 3),
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                LinkedInvoiceId = invoiceId,
                SettlementAmount = 30_000m,
                BankReceipt = 30_000m,
                ReceiptTotal = 30_000m,
                Note = "atomic payment",
                Revision = 3,
                IsDirty = true,
                CreatedAtUtc = preparedAtUtc.AddMinutes(-2),
                UpdatedAtUtc = preparedAtUtc
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = sharedId,
                AttachmentType = "기타",
                FileName = "atomic-accepted.pdf",
                StoredFileName = "atomic-accepted.pdf",
                MimeType = "application/pdf",
                FileSize = 1,
                FileHash = "A",
                Revision = 3,
                IsDirty = true,
                CreatedAtUtc = preparedAtUtc.AddMinutes(-2),
                UpdatedAtUtc = preparedAtUtc
            });
            db.SyncOutboxEntries.AddRange(
                new LocalSyncOutboxEntry
                {
                    Id = Guid.NewGuid(),
                    MutationId = paymentMutationId,
                    DeviceId = "test-device",
                    EntityName = nameof(LocalPayment),
                    EntityId = sharedId,
                    ExpectedRevision = 3,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BusinessDatabaseName = "USENET",
                    SessionId = session.SessionId,
                    UserId = session.User!.UserId,
                    Status = "Sent",
                    PreparedAtUtc = preparedAtUtc
                },
                new LocalSyncOutboxEntry
                {
                    Id = Guid.NewGuid(),
                    MutationId = transactionMutationId,
                    DeviceId = "test-device",
                    EntityName = nameof(LocalTransaction),
                    EntityId = sharedId,
                    ExpectedRevision = 3,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BusinessDatabaseName = "USENET",
                    SessionId = session.SessionId,
                    UserId = session.User!.UserId,
                    Status = "Sent",
                    PreparedAtUtc = preparedAtUtc
                },
                new LocalSyncOutboxEntry
                {
                    Id = Guid.NewGuid(),
                    MutationId = attachmentMutationId,
                    DeviceId = "test-device",
                    EntityName = nameof(LocalTransactionAttachment),
                    EntityId = attachmentId,
                    ExpectedRevision = 3,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BusinessDatabaseName = "USENET",
                    SessionId = session.SessionId,
                    UserId = session.User!.UserId,
                    Status = "Sent",
                    PreparedAtUtc = preparedAtUtc
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var payment = LocalMappings.ToDto(await db.Payments.AsNoTracking().SingleAsync(current => current.Id == sharedId));
            payment.ExpectedRevision = payment.Revision;
            payment.MutationId = paymentMutationId;
            payment.MutationCreatedAtUtc = payment.UpdatedAtUtc;
            var transaction = LocalMappings.ToDto(await db.Transactions.AsNoTracking().SingleAsync(current => current.Id == sharedId));
            transaction.ExpectedRevision = transaction.Revision;
            transaction.MutationId = transactionMutationId;
            transaction.MutationCreatedAtUtc = transaction.UpdatedAtUtc;
            var attachment = LocalMappings.ToDto(await db.TransactionAttachments.AsNoTracking().SingleAsync(current => current.Id == attachmentId));
            attachment.ExpectedRevision = attachment.Revision;
            attachment.MutationId = attachmentMutationId;
            attachment.MutationCreatedAtUtc = attachment.UpdatedAtUtc;
            var preparedRequest = new SyncPushRequest
            {
                DeviceId = "test-device",
                Payments = [payment],
                Transactions = [transaction],
                TransactionAttachments = [attachment]
            };

            using var sync = CreateSyncService(db, session);
            await InvokeApplyAcceptedRevisionsAsync(sync, [], preparedRequest);
            await InvokeMarkOutboxAcknowledgedAsync(sync, preparedRequest, []);

            var preservedOutbox = await db.SyncOutboxEntries.AsNoTracking().ToListAsync();
            Assert.All(preservedOutbox, entry => Assert.NotEqual("Acknowledged", entry.Status));
            Assert.True((await db.Payments.AsNoTracking().SingleAsync(current => current.Id == sharedId)).IsDirty);
            Assert.True((await db.Transactions.AsNoTracking().SingleAsync(current => current.Id == sharedId)).IsDirty);
            Assert.True((await db.TransactionAttachments.AsNoTracking().SingleAsync(current => current.Id == attachmentId)).IsDirty);

            var acceptedUpdatedAtUtc = preparedAtUtc.AddMinutes(1);
            var acceptedRevisions = new List<SyncAcceptedRevisionDto>
            {
                new()
                {
                    EntityName = "Payment",
                    EntityId = sharedId,
                    Revision = 11,
                    UpdatedAtUtc = acceptedUpdatedAtUtc
                },
                new()
                {
                    EntityName = "TransactionRecord",
                    EntityId = sharedId,
                    Revision = 12,
                    UpdatedAtUtc = acceptedUpdatedAtUtc
                },
                new()
                {
                    EntityName = "TransactionAttachment",
                    EntityId = attachmentId,
                    Revision = 13,
                    UpdatedAtUtc = acceptedUpdatedAtUtc
                }
            };

            await InvokeApplyAcceptedRevisionsAsync(sync, acceptedRevisions, preparedRequest);
            await InvokeMarkOutboxAcknowledgedAsync(sync, preparedRequest, acceptedRevisions);

            var acknowledgedOutbox = await db.SyncOutboxEntries.AsNoTracking()
                .ToDictionaryAsync(entry => entry.EntityName, entry => entry);
            Assert.Equal("Acknowledged", acknowledgedOutbox[nameof(LocalPayment)].Status);
            Assert.Equal(11, acknowledgedOutbox[nameof(LocalPayment)].AcceptedRevision);
            Assert.Equal("Acknowledged", acknowledgedOutbox[nameof(LocalTransaction)].Status);
            Assert.Equal(12, acknowledgedOutbox[nameof(LocalTransaction)].AcceptedRevision);
            Assert.Equal("Acknowledged", acknowledgedOutbox[nameof(LocalTransactionAttachment)].Status);
            Assert.Equal(13, acknowledgedOutbox[nameof(LocalTransactionAttachment)].AcceptedRevision);

            var acceptedPayment = await db.Payments.AsNoTracking().SingleAsync(current => current.Id == sharedId);
            var acceptedTransaction = await db.Transactions.AsNoTracking().SingleAsync(current => current.Id == sharedId);
            var acceptedAttachment = await db.TransactionAttachments.AsNoTracking().SingleAsync(current => current.Id == attachmentId);
            Assert.False(acceptedPayment.IsDirty);
            Assert.Equal(11, acceptedPayment.Revision);
            Assert.False(acceptedTransaction.IsDirty);
            Assert.Equal(12, acceptedTransaction.Revision);
            Assert.False(acceptedAttachment.IsDirty);
            Assert.Equal(13, acceptedAttachment.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ItemAcceptedAlias_AcknowledgesTemporaryOutboxAndCanonicalizesItemReferences()
    {
        PrepareAppRoot("georaeplan-item-accepted-alias");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var temporaryItemId = Guid.NewGuid();
            var canonicalItemId = Guid.NewGuid();
            var rentalAssetId = Guid.NewGuid();
            var priceGradeId = Guid.NewGuid();
            var priceGradeOptionId = Guid.NewGuid();
            var itemOutboxId = Guid.NewGuid();
            var priceGradeOutboxId = Guid.NewGuid();
            var acceptedUpdatedAtUtc = DateTime.UtcNow;
            var originalPriceGradeUpdatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-15);
            var originalStockUpdatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-25);
            var mutationId = $"test-device:{nameof(LocalItem)}:{temporaryItemId:N}:accepted-alias";

            db.Items.Add(new LocalItem
            {
                Id = temporaryItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "임시 렌탈 품목",
                NameMatchKey = "임시 렌탈 품목",
                SpecificationOriginal = "IMC2010",
                SpecificationMatchKey = "IMC2010",
                ItemKind = ItemKinds.Asset,
                TrackingType = ItemTrackingTypes.Asset,
                MaterialNumber = "ALIAS-ITEM-001",
                SerialNumber = "ALIAS-SERIAL-001",
                IsRental = true,
                CatalogExtensionSyncPending = true,
                Revision = 0,
                IsDirty = true,
                CreatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-30),
                UpdatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-20)
            });
            db.RentalAssets.Add(new LocalRentalAsset
            {
                Id = rentalAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                AssetKey = $"ALIAS-ASSET-{rentalAssetId:N}",
                ItemId = temporaryItemId,
                ItemName = "임시 렌탈 품목",
                Revision = 0,
                IsDirty = true,
                CreatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-30),
                UpdatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-20)
            });
            db.PriceGradeOptions.Add(new LocalPriceGradeOption
            {
                Id = priceGradeOptionId,
                Name = "ALIAS-GRADE",
                Revision = 5,
                IsDirty = false,
                CreatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-40),
                UpdatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-35)
            });
            db.ItemPriceGrades.Add(new LocalItemPriceGrade
            {
                Id = priceGradeId,
                ItemId = temporaryItemId,
                PriceGradeOptionId = priceGradeOptionId,
                PriceGradeName = "ALIAS-GRADE",
                UnitPrice = 123_456m,
                IsActive = true,
                Revision = 7,
                IsDirty = true,
                CreatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-30),
                UpdatedAtUtc = originalPriceGradeUpdatedAtUtc
            });
            db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = temporaryItemId,
                WarehouseCode = DomainConstants.WarehouseUsenetMain,
                Quantity = 12.5m,
                UpdatedAtUtc = originalStockUpdatedAtUtc,
                Revision = 19
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = itemOutboxId,
                MutationId = mutationId,
                DeviceId = "test-device",
                EntityName = nameof(LocalItem),
                EntityId = temporaryItemId,
                ExpectedRevision = 0,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                BusinessDatabaseName = "USENET",
                SessionId = session.SessionId,
                UserId = session.User!.UserId,
                Status = "Sent",
                PreparedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-10),
                SentAtUtc = acceptedUpdatedAtUtc.AddMinutes(-5)
            });
            db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
            {
                Id = priceGradeOutboxId,
                MutationId =
                    $"test-device:{nameof(LocalItemPriceGrade)}:{priceGradeId:N}:7:" +
                    $"{originalPriceGradeUpdatedAtUtc.Ticks}:0",
                DeviceId = "test-device",
                EntityName = nameof(LocalItemPriceGrade),
                EntityId = priceGradeId,
                ExpectedRevision = 7,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                Status = "Acknowledged",
                PreparedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-9),
                SentAtUtc = acceptedUpdatedAtUtc.AddMinutes(-4),
                AcknowledgedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-3)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var acceptedRevisions = new List<SyncAcceptedRevisionDto>
            {
                new()
                {
                    EntityName = "Item",
                    EntityId = canonicalItemId,
                    Revision = 42,
                    UpdatedAtUtc = acceptedUpdatedAtUtc
                },
                new()
                {
                    EntityName = "Item",
                    EntityId = temporaryItemId,
                    Revision = 42,
                    UpdatedAtUtc = acceptedUpdatedAtUtc
                },
                new()
                {
                    EntityName = "RentalAsset",
                    EntityId = rentalAssetId,
                    Revision = 42,
                    UpdatedAtUtc = acceptedUpdatedAtUtc
                }
            };

            var preparedItem = LocalMappings.ToDto(
                await db.Items
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(item => item.Id == temporaryItemId));
            preparedItem.ExpectedRevision = preparedItem.Revision;
            preparedItem.MutationCreatedAtUtc = preparedItem.UpdatedAtUtc;
            preparedItem.MutationId = mutationId;
            var preparedRequest = new SyncPushRequest
            {
                DeviceId = "test-device",
                Items =
                {
                    preparedItem
                }
            };

            await InvokeApplyAcceptedRevisionsAsync(sync, acceptedRevisions, preparedRequest);
            await InvokeMarkOutboxAcknowledgedAsync(
                sync,
                preparedRequest,
                acceptedRevisions);

            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, itemOutboxId));
            Assert.Equal("Acknowledged", await ReadOutboxStatusAsync(db, priceGradeOutboxId));
            var acceptedTemporaryItem = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == temporaryItemId);
            Assert.False(acceptedTemporaryItem.IsDirty);
            Assert.Equal(42, acceptedTemporaryItem.Revision);

            await InvokeUpsertPulledItemsAsync(
                sync,
                new SyncPullResponse
                {
                    Items =
                    [
                        new ItemDto
                        {
                            Id = canonicalItemId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            NameOriginal = "임시 렌탈 품목",
                            NameMatchKey = "임시 렌탈 품목",
                            SpecificationOriginal = "IMC2010",
                            SpecificationMatchKey = "IMC2010",
                            ItemKind = ItemKinds.Asset,
                            TrackingType = ItemTrackingTypes.Asset,
                            MaterialNumber = "ALIAS-ITEM-001",
                            SerialNumber = "ALIAS-SERIAL-001",
                            IsRental = true,
                            Revision = 43,
                            CreatedAtUtc = acceptedUpdatedAtUtc.AddMinutes(-30),
                            UpdatedAtUtc = acceptedUpdatedAtUtc
                        }
                    ]
                });

            db.ChangeTracker.Clear();
            var canonicalItem = Assert.Single(await db.Items.IgnoreQueryFilters().AsNoTracking().ToListAsync());
            Assert.Equal(canonicalItemId, canonicalItem.Id);
            Assert.False(canonicalItem.IsDirty);
            Assert.Equal(
                canonicalItemId,
                (await db.RentalAssets.IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(asset => asset.Id == rentalAssetId)).ItemId);
            var remappedPriceGrade = await db.ItemPriceGrades
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal(priceGradeId, remappedPriceGrade.Id);
            Assert.Equal(canonicalItemId, remappedPriceGrade.ItemId);
            Assert.Equal(priceGradeOptionId, remappedPriceGrade.PriceGradeOptionId);
            Assert.True(remappedPriceGrade.IsDirty);
            Assert.Equal(7, remappedPriceGrade.Revision);
            Assert.True(remappedPriceGrade.UpdatedAtUtc > originalPriceGradeUpdatedAtUtc);

            var remappedStock = await db.ItemWarehouseStocks
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal(canonicalItemId, remappedStock.ItemId);
            Assert.Equal(DomainConstants.WarehouseUsenetMain, remappedStock.WarehouseCode);
            Assert.Equal(12.5m, remappedStock.Quantity);
            Assert.Equal(originalStockUpdatedAtUtc, remappedStock.UpdatedAtUtc);
            Assert.Equal(19, remappedStock.Revision);

            var priceGradeOutbox = await db.SyncOutboxEntries
                .AsNoTracking()
                .SingleAsync(entry => entry.Id == priceGradeOutboxId);
            Assert.Equal(nameof(LocalItemPriceGrade), priceGradeOutbox.EntityName);
            Assert.Equal(priceGradeId, priceGradeOutbox.EntityId);
            Assert.Equal("Acknowledged", priceGradeOutbox.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UpsertPulledItems_FullPullActiveCanonicalAndSameKeyTombstone_RemapsReferencesOneWayToCanonical()
    {
        PrepareAppRoot("georaeplan-item-pull-canonical-tombstone");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonicalItemId = Guid.NewGuid();
            var tombstoneItemId = Guid.NewGuid();
            var canonicalInvoiceLineSerialId = Guid.NewGuid();
            var tombstoneInvoiceLineSerialId = Guid.NewGuid();
            var canonicalSerialLedgerId = Guid.NewGuid();
            var tombstoneSerialLedgerId = Guid.NewGuid();
            var canonicalInventoryMovementId = Guid.NewGuid();
            var tombstoneInventoryMovementId = Guid.NewGuid();
            var canonicalStockLayerId = Guid.NewGuid();
            var tombstoneStockLayerId = Guid.NewGuid();
            var billingProfileId = Guid.NewGuid();
            var malformedBillingProfileId = Guid.NewGuid();
            var unknownShapeBillingProfileId = Guid.NewGuid();
            var templateItemId = Guid.NewGuid();
            var representativeAssetId = Guid.NewGuid();
            var includedAssetIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var now = DateTime.UtcNow;

            LocalItem CreateLocalItem(Guid id) => new()
            {
                Id = id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "canonical tombstone cycle item",
                NameMatchKey = "canonical tombstone cycle item",
                SpecificationOriginal = "CYCLE-SPEC",
                SpecificationMatchKey = "CYCLE-SPEC",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                MaterialNumber = "CYCLE-MATERIAL-001",
                SerialNumber = "CYCLE-SERIAL-001",
                Revision = 40,
                IsDirty = false,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddDays(-1)
            };

            db.Items.AddRange(
                CreateLocalItem(canonicalItemId),
                CreateLocalItem(tombstoneItemId));
            db.InvoiceLineSerials.AddRange(
                new LocalInvoiceLineSerial
                {
                    Id = canonicalInvoiceLineSerialId,
                    InvoiceId = Guid.NewGuid(),
                    InvoiceLineId = Guid.NewGuid(),
                    ItemId = canonicalItemId,
                    SerialNumber = "CYCLE-ILS-CANONICAL"
                },
                new LocalInvoiceLineSerial
                {
                    Id = tombstoneInvoiceLineSerialId,
                    InvoiceId = Guid.NewGuid(),
                    InvoiceLineId = Guid.NewGuid(),
                    ItemId = tombstoneItemId,
                    SerialNumber = "CYCLE-ILS-TOMBSTONE"
                });
            db.SerialLedgers.AddRange(
                new LocalSerialLedger
                {
                    Id = canonicalSerialLedgerId,
                    ItemId = canonicalItemId,
                    SerialNumber = "CYCLE-LEDGER-CANONICAL",
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Status = "Available"
                },
                new LocalSerialLedger
                {
                    Id = tombstoneSerialLedgerId,
                    ItemId = tombstoneItemId,
                    SerialNumber = "CYCLE-LEDGER-TOMBSTONE",
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Status = "Available"
                });
            db.InventoryMovements.AddRange(
                new LocalInventoryMovement
                {
                    Id = canonicalInventoryMovementId,
                    ItemId = canonicalItemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    MovementType = "CycleRegression",
                    QuantityDelta = 1m,
                    OccurredDate = new DateOnly(2026, 8, 8)
                },
                new LocalInventoryMovement
                {
                    Id = tombstoneInventoryMovementId,
                    ItemId = tombstoneItemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    MovementType = "CycleRegression",
                    QuantityDelta = 1m,
                    OccurredDate = new DateOnly(2026, 8, 8)
                });
            db.StockLayers.AddRange(
                new LocalStockLayer
                {
                    Id = canonicalStockLayerId,
                    ItemId = canonicalItemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ReceiptDate = new DateOnly(2026, 8, 8),
                    OriginalQuantity = 1m,
                    RemainingQuantity = 1m
                },
                new LocalStockLayer
                {
                    Id = tombstoneStockLayerId,
                    ItemId = tombstoneItemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    ReceiptDate = new DateOnly(2026, 8, 8),
                    OriginalQuantity = 1m,
                    RemainingQuantity = 1m
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            ItemDto CreatePulledItem(Guid id, bool isDeleted, long revision) => new()
            {
                Id = id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "canonical tombstone cycle item",
                NameMatchKey = "canonical tombstone cycle item",
                SpecificationOriginal = "CYCLE-SPEC",
                SpecificationMatchKey = "CYCLE-SPEC",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                MaterialNumber = "CYCLE-MATERIAL-001",
                SerialNumber = "CYCLE-SERIAL-001",
                Revision = revision,
                IsDeleted = isDeleted,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now
            };

            const string malformedTemplateJson = "{not-json";
            const string unknownShapeTemplateJson = "{\"FutureTemplateVersion\":2}";
            var incomingTemplateJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    ItemId = templateItemId,
                    CatalogItemId = tombstoneItemId,
                    DisplayItemName = "server supplied display name",
                    Specification = "server supplied specification",
                    RepresentativeAssetId = representativeAssetId,
                    IncludedAssetIds = includedAssetIds,
                    FutureTemplateProperty = "preserve-me"
                }
            });

            RentalBillingProfileDto CreatePulledProfile(
                Guid id,
                string profileKey,
                string templateJson) => new()
            {
                Id = id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = profileKey,
                CustomerName = $"canonical item profile {profileKey}",
                BusinessNumber = profileKey,
                ItemName = "canonical tombstone cycle item",
                BillingType = "개별",
                BillingAdvanceMode = "후불",
                BillingMethod = "전자세금계산서",
                BillingDay = 28,
                BillingCycleMonths = 1,
                MonthlyAmount = 10_000m,
                BillingTemplateJson = templateJson,
                IsActive = true,
                Revision = 61,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            };

            using var sync = CreateSyncService(db, CreateAdminSession());
            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    Items =
                    [
                        CreatePulledItem(canonicalItemId, isDeleted: false, revision: 51),
                        CreatePulledItem(tombstoneItemId, isDeleted: true, revision: 52)
                    ],
                    RentalBillingProfiles =
                    [
                        CreatePulledProfile(
                            billingProfileId,
                            "CATALOG-ALIAS-PROFILE",
                            incomingTemplateJson),
                        CreatePulledProfile(
                            malformedBillingProfileId,
                            "CATALOG-ALIAS-MALFORMED",
                            malformedTemplateJson),
                        CreatePulledProfile(
                            unknownShapeBillingProfileId,
                            "CATALOG-ALIAS-UNKNOWN",
                            unknownShapeTemplateJson)
                    ]
                });

            db.ChangeTracker.Clear();
            var pulledItems = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .ToDictionaryAsync(item => item.Id);
            Assert.False(pulledItems[canonicalItemId].IsDeleted);
            Assert.True(pulledItems[tombstoneItemId].IsDeleted);

            var invoiceLineSerialItemIds = await db.InvoiceLineSerials
                .AsNoTracking()
                .ToDictionaryAsync(serial => serial.Id, serial => serial.ItemId);
            Assert.Equal(canonicalItemId, invoiceLineSerialItemIds[canonicalInvoiceLineSerialId]);
            Assert.Equal(canonicalItemId, invoiceLineSerialItemIds[tombstoneInvoiceLineSerialId]);

            var serialLedgerItemIds = await db.SerialLedgers
                .AsNoTracking()
                .ToDictionaryAsync(ledger => ledger.Id, ledger => ledger.ItemId);
            Assert.Equal(canonicalItemId, serialLedgerItemIds[canonicalSerialLedgerId]);
            Assert.Equal(canonicalItemId, serialLedgerItemIds[tombstoneSerialLedgerId]);

            var inventoryMovementItemIds = await db.InventoryMovements
                .AsNoTracking()
                .ToDictionaryAsync(movement => movement.Id, movement => movement.ItemId);
            Assert.Equal(canonicalItemId, inventoryMovementItemIds[canonicalInventoryMovementId]);
            Assert.Equal(canonicalItemId, inventoryMovementItemIds[tombstoneInventoryMovementId]);

            var stockLayerItemIds = await db.StockLayers
                .AsNoTracking()
                .ToDictionaryAsync(layer => layer.Id, layer => layer.ItemId);
            Assert.Equal(canonicalItemId, stockLayerItemIds[canonicalStockLayerId]);
            Assert.Equal(canonicalItemId, stockLayerItemIds[tombstoneStockLayerId]);

            var billingProfiles = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToDictionaryAsync(profile => profile.Id);
            using var remappedTemplate = JsonDocument.Parse(
                billingProfiles[billingProfileId].BillingTemplateJson);
            var templateItem = Assert.Single(
                remappedTemplate.RootElement.EnumerateArray());
            Assert.Equal(
                canonicalItemId,
                templateItem.GetProperty("CatalogItemId").GetGuid());
            Assert.Equal(
                templateItemId,
                templateItem.GetProperty("ItemId").GetGuid());
            Assert.Equal(
                representativeAssetId,
                templateItem.GetProperty("RepresentativeAssetId").GetGuid());
            Assert.Equal(
                includedAssetIds,
                templateItem.GetProperty("IncludedAssetIds")
                    .EnumerateArray()
                    .Select(value => value.GetGuid())
                    .ToArray());
            Assert.Equal(
                "server supplied display name",
                templateItem.GetProperty("DisplayItemName").GetString());
            Assert.Equal(
                "server supplied specification",
                templateItem.GetProperty("Specification").GetString());
            Assert.Equal(
                "preserve-me",
                templateItem.GetProperty("FutureTemplateProperty").GetString());
            Assert.Equal(
                malformedTemplateJson,
                billingProfiles[malformedBillingProfileId].BillingTemplateJson);
            Assert.Equal(
                unknownShapeTemplateJson,
                billingProfiles[unknownShapeBillingProfileId].BillingTemplateJson);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UpsertPulledItems_MixedCleanAndDirtyAliases_DoesNotCreateDanglingCanonicalReferences()
    {
        PrepareAppRoot("georaeplan-item-pull-mixed-alias-conflict");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var cleanAliasId = Guid.NewGuid();
            var dirtyAliasId = Guid.NewGuid();
            var incomingCanonicalId = Guid.NewGuid();
            var serialId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var now = DateTime.UtcNow;

            LocalItem CreateAlias(Guid id, bool isDirty) => new()
            {
                Id = id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "mixed alias conflict item",
                NameMatchKey = "mixed alias conflict item",
                SpecificationOriginal = "MIXED-ALIAS-SPEC",
                SpecificationMatchKey = "MIXED-ALIAS-SPEC",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                MaterialNumber = "MIXED-ALIAS-MATERIAL",
                SerialNumber = "MIXED-ALIAS-SERIAL",
                Revision = isDirty ? 12 : 11,
                IsDirty = isDirty,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = isDirty ? now : now.AddDays(-1)
            };

            var cleanAlias = CreateAlias(cleanAliasId, isDirty: false);
            var dirtyAlias = CreateAlias(dirtyAliasId, isDirty: true);
            var originalDirtyUpdatedAtUtc = dirtyAlias.UpdatedAtUtc;
            var originalTemplateJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    CatalogItemId = cleanAliasId,
                    FutureTemplateProperty = "must-stay-on-clean-alias"
                }
            });
            db.Items.AddRange(cleanAlias, dirtyAlias);
            db.InvoiceLineSerials.Add(new LocalInvoiceLineSerial
            {
                Id = serialId,
                InvoiceId = Guid.NewGuid(),
                InvoiceLineId = Guid.NewGuid(),
                ItemId = cleanAliasId,
                SerialNumber = "MIXED-ALIAS-REFERENCE"
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "MIXED-ALIAS-PROFILE",
                CustomerName = "mixed alias customer",
                ItemName = "mixed alias conflict item",
                BillingTemplateJson = originalTemplateJson,
                IsDirty = false,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddDays(-1)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            await InvokeUpsertPulledItemsAsync(
                sync,
                new SyncPullResponse
                {
                    Items =
                    [
                        new ItemDto
                        {
                            Id = incomingCanonicalId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            NameOriginal = "mixed alias conflict item",
                            NameMatchKey = "mixed alias conflict item",
                            SpecificationOriginal = "MIXED-ALIAS-SPEC",
                            SpecificationMatchKey = "MIXED-ALIAS-SPEC",
                            ItemKind = ItemKinds.Product,
                            TrackingType = ItemTrackingTypes.Stock,
                            MaterialNumber = "MIXED-ALIAS-MATERIAL",
                            SerialNumber = "MIXED-ALIAS-SERIAL",
                            Revision = 20,
                            CreatedAtUtc = now.AddDays(-2),
                            UpdatedAtUtc = now.AddMinutes(1)
                        }
                    ]
                });

            db.ChangeTracker.Clear();
            var items = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .ToDictionaryAsync(item => item.Id);
            Assert.Equal(2, items.Count);
            Assert.Contains(cleanAliasId, items.Keys);
            Assert.Contains(dirtyAliasId, items.Keys);
            Assert.DoesNotContain(incomingCanonicalId, items.Keys);
            Assert.True(items[dirtyAliasId].IsDirty);
            Assert.Equal(originalDirtyUpdatedAtUtc, items[dirtyAliasId].UpdatedAtUtc);
            Assert.Equal(
                cleanAliasId,
                (await db.InvoiceLineSerials.AsNoTracking()
                    .SingleAsync(serial => serial.Id == serialId)).ItemId);
            Assert.Equal(
                originalTemplateJson,
                (await db.RentalBillingProfiles.IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(profile => profile.Id == profileId)).BillingTemplateJson);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("malformed-unicode")]
    [InlineData("malformed-x")]
    [InlineData("malformed-unicode-x")]
    [InlineData("non-array-unicode")]
    [InlineData("ambiguous-lower-first")]
    [InlineData("ambiguous-upper-first")]
    [InlineData("mixed-non-object")]
    [InlineData("known-and-future-alias")]
    [InlineData("null-and-future-alias")]
    [InlineData("missing-and-future-alias")]
    public async Task UpsertPulledItems_TargetBearingUnsupportedTemplate_BlocksAliasDeletion(
        string templateMode)
    {
        PrepareAppRoot($"georaeplan-item-alias-template-block-{templateMode}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var aliasItemId = Guid.NewGuid();
            var incomingCanonicalId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var aliasText = aliasItemId.ToString("D");
            var unicodeEscapedAliasText = string.Concat(
                aliasText.Select(character => $"\\u{(int)character:x4}"));
            var aliasXText = aliasItemId.ToString("X");
            var unicodeEscapedAliasXText = string.Concat(
                aliasXText.Select(character => $"\\u{(int)character:x4}"));
            var originalTemplateJson = templateMode switch
            {
                "malformed" =>
                    $"[{{\"CatalogItemId\":\"{aliasText}\",\"Broken\":]",
                "malformed-unicode" =>
                    $"[{{\"CatalogItemId\":\"{unicodeEscapedAliasText}\",\"Broken\":]",
                "malformed-x" =>
                    $"[{{\"CatalogItemId\":\"{aliasXText}\",\"Broken\":]",
                "malformed-unicode-x" =>
                    $"[{{\"CatalogItemId\":\"{unicodeEscapedAliasXText}\",\"Broken\":]",
                "non-array-unicode" =>
                    $"{{\"CatalogItemId\":\"{unicodeEscapedAliasText}\",\"FutureRoot\":true}}",
                "ambiguous-lower-first" =>
                    $"[{{\"catalogitemid\":\"{aliasText}\",\"CatalogItemId\":\"{aliasText}\"}}]",
                "ambiguous-upper-first" =>
                    $"[{{\"CatalogItemId\":\"{aliasText}\",\"catalogitemid\":\"{aliasText}\"}}]",
                "mixed-non-object" =>
                    $"[\"{aliasText}\",{{\"CatalogItemId\":\"{aliasText}\"}}]",
                "known-and-future-alias" =>
                    $"[{{\"CatalogItemId\":\"{incomingCanonicalId:D}\",\"FutureItemReference\":\"{aliasText}\"}}]",
                "null-and-future-alias" =>
                    $"[{{\"CatalogItemId\":null,\"FutureItemReference\":\"{aliasText}\"}}]",
                "missing-and-future-alias" =>
                    $"[{{\"FutureItemReference\":\"{aliasText}\"}}]",
                _ => throw new ArgumentOutOfRangeException(nameof(templateMode))
            };

            db.Items.Add(new LocalItem
            {
                Id = aliasItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "unsupported template alias item",
                NameMatchKey = "unsupported template alias item",
                SpecificationOriginal = "UNSUPPORTED-TEMPLATE-SPEC",
                SpecificationMatchKey = "UNSUPPORTED-TEMPLATE-SPEC",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                MaterialNumber = "UNSUPPORTED-TEMPLATE-MATERIAL",
                SerialNumber = "UNSUPPORTED-TEMPLATE-SERIAL",
                Revision = 10,
                IsDirty = false,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddDays(-1)
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"UNSUPPORTED-TEMPLATE-{templateMode}",
                CustomerName = "unsupported template customer",
                ItemName = "unsupported template alias item",
                BillingTemplateJson = originalTemplateJson,
                IsDirty = false,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddDays(-1)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeUpsertPulledItemsAsync(
                    sync,
                    new SyncPullResponse
                    {
                        Items =
                        [
                            new ItemDto
                            {
                                Id = incomingCanonicalId,
                                TenantCode = TenantScopeCatalog.UsenetGroup,
                                OfficeCode = OfficeCodeCatalog.Usenet,
                                NameOriginal = "unsupported template alias item",
                                NameMatchKey = "unsupported template alias item",
                                SpecificationOriginal = "UNSUPPORTED-TEMPLATE-SPEC",
                                SpecificationMatchKey = "UNSUPPORTED-TEMPLATE-SPEC",
                                ItemKind = ItemKinds.Product,
                                TrackingType = ItemTrackingTypes.Stock,
                                MaterialNumber = "UNSUPPORTED-TEMPLATE-MATERIAL",
                                SerialNumber = "UNSUPPORTED-TEMPLATE-SERIAL",
                                Revision = 20,
                                CreatedAtUtc = now.AddDays(-2),
                                UpdatedAtUtc = now
                            }
                        ]
                    }));

            Assert.Contains("청구 템플릿", failure.Message, StringComparison.Ordinal);
            db.ChangeTracker.Clear();
            var storedItems = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .ToDictionaryAsync(item => item.Id);
            Assert.Single(storedItems);
            Assert.Contains(aliasItemId, storedItems.Keys);
            Assert.DoesNotContain(incomingCanonicalId, storedItems.Keys);
            Assert.Equal(
                originalTemplateJson,
                (await db.RentalBillingProfiles.IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(profile => profile.Id == profileId)).BillingTemplateJson);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("malformed-unicode")]
    [InlineData("non-array-unicode")]
    [InlineData("unicode-x")]
    [InlineData("known-and-future-alias")]
    public async Task ApplyPull_TargetBearingUnsupportedIncomingTemplate_RollsBackAliasDeletion(
        string templateMode)
    {
        PrepareAppRoot($"georaeplan-incoming-item-alias-template-block-{templateMode}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var aliasItemId = Guid.NewGuid();
            var incomingCanonicalId = Guid.NewGuid();
            var incomingProfileId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            var aliasText = aliasItemId.ToString("D");
            var unicodeEscapedAliasText = string.Concat(
                aliasText.Select(character => $"\\u{(int)character:x4}"));
            var unicodeEscapedAliasXText = string.Concat(
                aliasItemId.ToString("X").Select(character => $"\\u{(int)character:x4}"));
            var unsupportedTemplateJson = templateMode switch
            {
                "malformed-unicode" =>
                    $"[{{\"catalogitemid\":\"{unicodeEscapedAliasText}\",\"Broken\":]",
                "non-array-unicode" =>
                    $"{{\"catalogitemid\":\"{unicodeEscapedAliasText}\",\"FutureRoot\":true}}",
                "unicode-x" =>
                    $"[{{\"FutureItemReference\":\"{unicodeEscapedAliasXText}\"}}]",
                "known-and-future-alias" =>
                    $"[{{\"CatalogItemId\":\"{incomingCanonicalId:D}\",\"FutureItemReference\":\"{aliasText}\"}}]",
                _ => throw new ArgumentOutOfRangeException(nameof(templateMode))
            };
            db.Items.Add(new LocalItem
            {
                Id = aliasItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "incoming unsupported template alias",
                NameMatchKey = "incoming unsupported template alias",
                SpecificationOriginal = "INCOMING-UNSUPPORTED-SPEC",
                SpecificationMatchKey = "INCOMING-UNSUPPORTED-SPEC",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                MaterialNumber = "INCOMING-UNSUPPORTED-MATERIAL",
                SerialNumber = "INCOMING-UNSUPPORTED-SERIAL",
                Revision = 10,
                IsDirty = false,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddDays(-1)
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            var failure = await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokeApplyPullAsync(
                    sync,
                    new SyncPullResponse
                    {
                        Items =
                        [
                            new ItemDto
                            {
                                Id = incomingCanonicalId,
                                TenantCode = TenantScopeCatalog.UsenetGroup,
                                OfficeCode = OfficeCodeCatalog.Usenet,
                                NameOriginal = "incoming unsupported template alias",
                                NameMatchKey = "incoming unsupported template alias",
                                SpecificationOriginal = "INCOMING-UNSUPPORTED-SPEC",
                                SpecificationMatchKey = "INCOMING-UNSUPPORTED-SPEC",
                                ItemKind = ItemKinds.Product,
                                TrackingType = ItemTrackingTypes.Stock,
                                MaterialNumber = "INCOMING-UNSUPPORTED-MATERIAL",
                                SerialNumber = "INCOMING-UNSUPPORTED-SERIAL",
                                Revision = 20,
                                CreatedAtUtc = now.AddDays(-2),
                                UpdatedAtUtc = now
                            }
                        ],
                        RentalBillingProfiles =
                        [
                            new RentalBillingProfileDto
                            {
                                Id = incomingProfileId,
                                TenantCode = TenantScopeCatalog.UsenetGroup,
                                OfficeCode = OfficeCodeCatalog.Usenet,
                                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                                ProfileKey = "INCOMING-UNSUPPORTED-PROFILE",
                                CustomerName = "incoming unsupported customer",
                                ItemName = "incoming unsupported template alias",
                                BillingTemplateJson = unsupportedTemplateJson,
                                IsActive = true,
                                Revision = 21,
                                CreatedAtUtc = now.AddDays(-1),
                                UpdatedAtUtc = now
                            }
                        ]
                    }));

            Assert.Contains("청구 템플릿", failure.Message, StringComparison.Ordinal);
            db.ChangeTracker.Clear();
            var storedItems = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .ToDictionaryAsync(item => item.Id);
            Assert.Single(storedItems);
            Assert.Contains(aliasItemId, storedItems.Keys);
            Assert.DoesNotContain(incomingCanonicalId, storedItems.Keys);
            Assert.False(await db.RentalBillingProfiles.IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(profile => profile.Id == incomingProfileId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TrySyncAsync_ResidualTemplateAlias_RollsBackCanonicalItemProfileAndRevisionCursor()
    {
        PrepareAppRoot("georaeplan-runtime-item-alias-template-residual");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var aliasItemId = Guid.NewGuid();
            var incomingCanonicalId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            var originalTemplateJson =
                $"[{{\"CatalogItemId\":\"{incomingCanonicalId:D}\",\"FutureItemReference\":\"{aliasItemId:D}\"}}]";
            db.Items.Add(new LocalItem
            {
                Id = aliasItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "runtime residual template alias",
                NameMatchKey = "runtime residual template alias",
                SpecificationOriginal = "RUNTIME-RESIDUAL-SPEC",
                SpecificationMatchKey = "RUNTIME-RESIDUAL-SPEC",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                MaterialNumber = "RUNTIME-RESIDUAL-MATERIAL",
                SerialNumber = "RUNTIME-RESIDUAL-SERIAL",
                Revision = 10,
                IsDirty = false,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddDays(-1)
            });
            db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = "RUNTIME-RESIDUAL-PROFILE",
                CustomerName = "runtime residual customer",
                ItemName = "runtime residual template alias",
                BillingTemplateJson = originalTemplateJson,
                IsDirty = false,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddDays(-1)
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 20,
                    Items =
                    [
                        new ItemDto
                        {
                            Id = incomingCanonicalId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            NameOriginal = "runtime residual template alias",
                            NameMatchKey = "runtime residual template alias",
                            SpecificationOriginal = "RUNTIME-RESIDUAL-SPEC",
                            SpecificationMatchKey = "RUNTIME-RESIDUAL-SPEC",
                            ItemKind = ItemKinds.Product,
                            TrackingType = ItemTrackingTypes.Stock,
                            MaterialNumber = "RUNTIME-RESIDUAL-MATERIAL",
                            SerialNumber = "RUNTIME-RESIDUAL-SERIAL",
                            Revision = 20,
                            CreatedAtUtc = now.AddDays(-2),
                            UpdatedAtUtc = now
                        }
                    ]
                });
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.ReleasePull();

            Assert.False(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(1, handler.PullCount);

            await using var verificationDb = new LocalDbContext();
            var storedItems = await verificationDb.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .ToDictionaryAsync(item => item.Id);
            var preservedAlias = Assert.Single(storedItems).Value;
            Assert.Equal(aliasItemId, preservedAlias.Id);
            Assert.Equal(10, preservedAlias.Revision);
            Assert.False(preservedAlias.IsDirty);
            Assert.DoesNotContain(incomingCanonicalId, storedItems.Keys);
            Assert.Equal(
                originalTemplateJson,
                (await verificationDb.RentalBillingProfiles
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(profile => profile.Id == profileId))
                .BillingTemplateJson);
            Assert.Equal(
                "1",
                await verificationDb.Settings.AsNoTracking()
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
    public async Task UpsertPulledItems_ItemOnlyPullRemapsExistingRentalBillingTemplateCatalogReference()
    {
        PrepareAppRoot("georaeplan-item-only-profile-catalog-alias");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var canonicalItemId = Guid.NewGuid();
            var tombstoneItemId = Guid.NewGuid();
            var profileId = Guid.NewGuid();
            var malformedProfileId = Guid.NewGuid();
            var emptyProfileId = Guid.NewGuid();
            var templateItemId = Guid.NewGuid();
            var representativeAssetId = Guid.NewGuid();
            var includedAssetIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
            var now = DateTime.UtcNow;
            const string malformedTemplateJson = "{item-only-not-json";
            var existingTemplateJson = JsonSerializer.Serialize(new[]
            {
                new
                {
                    ItemId = templateItemId,
                    CatalogItemId = tombstoneItemId,
                    RepresentativeAssetId = representativeAssetId,
                    IncludedAssetIds = includedAssetIds,
                    FutureTemplateProperty = "keep-local-existing"
                }
            });

            LocalItem CreateLocalItem(Guid id) => new()
            {
                Id = id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "item only canonical alias",
                NameMatchKey = "item only canonical alias",
                SpecificationOriginal = "ITEM-ONLY-SPEC",
                SpecificationMatchKey = "ITEM-ONLY-SPEC",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                MaterialNumber = "ITEM-ONLY-MATERIAL-001",
                SerialNumber = "ITEM-ONLY-SERIAL-001",
                Revision = 70,
                IsDirty = false,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddDays(-1)
            };

            LocalRentalBillingProfile CreateLocalProfile(
                Guid id,
                string profileKey,
                string templateJson)
            {
                var local = LocalMappings.ToLocal(new RentalBillingProfileDto
                {
                    Id = id,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                    ProfileKey = profileKey,
                    CustomerName = profileKey,
                    BusinessNumber = profileKey,
                    ItemName = "item only canonical alias",
                    BillingType = "개별",
                    BillingAdvanceMode = "후불",
                    BillingMethod = "전자세금계산서",
                    BillingDay = 28,
                    BillingCycleMonths = 1,
                    MonthlyAmount = 10_000m,
                    BillingTemplateJson = templateJson,
                    IsActive = true,
                    Revision = 71,
                    CreatedAtUtc = now.AddDays(-1),
                    UpdatedAtUtc = now
                });
                local.IsDirty = false;
                return local;
            }

            db.Items.AddRange(
                CreateLocalItem(canonicalItemId),
                CreateLocalItem(tombstoneItemId));
            db.RentalBillingProfiles.AddRange(
                CreateLocalProfile(profileId, "ITEM-ONLY-PROFILE", existingTemplateJson),
                CreateLocalProfile(
                    malformedProfileId,
                    "ITEM-ONLY-MALFORMED",
                    malformedTemplateJson),
                CreateLocalProfile(emptyProfileId, "ITEM-ONLY-EMPTY", string.Empty));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            ItemDto CreatePulledItem(Guid id, bool isDeleted, long revision) => new()
            {
                Id = id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "item only canonical alias",
                NameMatchKey = "item only canonical alias",
                SpecificationOriginal = "ITEM-ONLY-SPEC",
                SpecificationMatchKey = "ITEM-ONLY-SPEC",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                MaterialNumber = "ITEM-ONLY-MATERIAL-001",
                SerialNumber = "ITEM-ONLY-SERIAL-001",
                Revision = revision,
                IsDeleted = isDeleted,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now
            };

            using var sync = CreateSyncService(db, CreateAdminSession());
            await InvokeUpsertPulledItemsAsync(
                sync,
                new SyncPullResponse
                {
                    Items =
                    [
                        CreatePulledItem(canonicalItemId, isDeleted: false, revision: 72),
                        CreatePulledItem(tombstoneItemId, isDeleted: true, revision: 73)
                    ]
                });

            db.ChangeTracker.Clear();
            var profiles = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToDictionaryAsync(profile => profile.Id);
            using var remappedTemplate = JsonDocument.Parse(
                profiles[profileId].BillingTemplateJson);
            var templateItem = Assert.Single(
                remappedTemplate.RootElement.EnumerateArray());
            Assert.Equal(
                canonicalItemId,
                templateItem.GetProperty("CatalogItemId").GetGuid());
            Assert.Equal(
                templateItemId,
                templateItem.GetProperty("ItemId").GetGuid());
            Assert.Equal(
                representativeAssetId,
                templateItem.GetProperty("RepresentativeAssetId").GetGuid());
            Assert.Equal(
                includedAssetIds,
                templateItem.GetProperty("IncludedAssetIds")
                    .EnumerateArray()
                    .Select(value => value.GetGuid())
                    .ToArray());
            Assert.Equal(
                "keep-local-existing",
                templateItem.GetProperty("FutureTemplateProperty").GetString());
            Assert.Equal(
                malformedTemplateJson,
                profiles[malformedProfileId].BillingTemplateJson);
            Assert.Equal(string.Empty, profiles[emptyProfileId].BillingTemplateJson);
            Assert.All(profiles.Values, profile => Assert.False(profile.IsDirty));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UpsertPulledItems_ItemOnlyPullAdvancesDirtyNestedRootMutationBoundaries()
    {
        PrepareAppRoot("georaeplan-item-only-dirty-root-mutation-boundary");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var canonicalItemId = Guid.NewGuid();
            var tombstoneItemId = Guid.NewGuid();
            var dirtyInvoiceId = Guid.NewGuid();
            var cleanInvoiceId = Guid.NewGuid();
            var dirtyTransferId = Guid.NewGuid();
            var cleanTransferId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var oldDirtyInvoiceUpdatedAtUtc = new DateTime(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc);
            var oldDirtyTransferUpdatedAtUtc = oldDirtyInvoiceUpdatedAtUtc.AddMinutes(1);
            var oldCleanInvoiceUpdatedAtUtc = oldDirtyInvoiceUpdatedAtUtc.AddMinutes(2);
            var oldCleanTransferUpdatedAtUtc = oldDirtyInvoiceUpdatedAtUtc.AddMinutes(3);
            const string deviceId = "item-alias-mutation-boundary-device";

            LocalItem CreateLocalItem(Guid id) => new()
            {
                Id = id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "nested root item alias",
                NameMatchKey = "nested root item alias",
                SpecificationOriginal = "NESTED-ROOT-SPEC",
                SpecificationMatchKey = "NESTED-ROOT-SPEC",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                MaterialNumber = "NESTED-ROOT-MATERIAL-001",
                SerialNumber = "NESTED-ROOT-SERIAL-001",
                Revision = 90,
                IsDirty = false,
                CreatedAtUtc = oldDirtyInvoiceUpdatedAtUtc.AddDays(-2),
                UpdatedAtUtc = oldDirtyInvoiceUpdatedAtUtc.AddDays(-1)
            };

            LocalInvoice CreateInvoice(Guid id, bool isDirty, DateTime updatedAtUtc) => new()
            {
                Id = id,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = $"INV-{id:N}",
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 8, 1),
                SourceWarehouseCode = DomainConstants.WarehouseUsenetMain,
                VersionGroupId = id,
                IsLatestVersion = true,
                IsConfirmed = true,
                Revision = 91,
                IsDirty = isDirty,
                CreatedAtUtc = updatedAtUtc.AddDays(-1),
                UpdatedAtUtc = updatedAtUtc,
                Lines =
                [
                    new LocalInvoiceLine
                    {
                        Id = Guid.NewGuid(),
                        InvoiceId = id,
                        ItemId = tombstoneItemId,
                        ItemNameOriginal = "nested root item alias",
                        SpecificationOriginal = "NESTED-ROOT-SPEC",
                        Unit = "개",
                        Quantity = 1m,
                        UnitPrice = 1_000m,
                        LineAmount = 1_000m,
                        OrderIndex = 1
                    }
                ]
            };

            LocalInventoryTransfer CreateTransfer(Guid id, bool isDirty, DateTime updatedAtUtc) => new()
            {
                Id = id,
                TransferNumber = $"TR-{id:N}",
                TransferDate = new DateOnly(2026, 8, 1),
                FromWarehouseCode = DomainConstants.WarehouseUsenetMain,
                ToWarehouseCode = DomainConstants.WarehouseYeonsuMain,
                Revision = 92,
                IsDirty = isDirty,
                CreatedAtUtc = updatedAtUtc.AddDays(-1),
                UpdatedAtUtc = updatedAtUtc,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = Guid.NewGuid(),
                        TransferId = id,
                        ItemId = tombstoneItemId,
                        ItemNameOriginal = "nested root item alias",
                        SpecificationOriginal = "NESTED-ROOT-SPEC",
                        Unit = "개",
                        Quantity = 1m
                    }
                ]
            };

            db.Items.AddRange(
                CreateLocalItem(canonicalItemId),
                CreateLocalItem(tombstoneItemId));
            db.Invoices.AddRange(
                CreateInvoice(dirtyInvoiceId, isDirty: true, oldDirtyInvoiceUpdatedAtUtc),
                CreateInvoice(cleanInvoiceId, isDirty: false, oldCleanInvoiceUpdatedAtUtc));
            db.InventoryTransfers.AddRange(
                CreateTransfer(dirtyTransferId, isDirty: true, oldDirtyTransferUpdatedAtUtc),
                CreateTransfer(cleanTransferId, isDirty: false, oldCleanTransferUpdatedAtUtc));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var dirtyInvoiceBeforePull = await db.Invoices.IgnoreQueryFilters()
                .Include(invoice => invoice.Lines)
                .SingleAsync(invoice => invoice.Id == dirtyInvoiceId);
            var dirtyTransferBeforePull = await db.InventoryTransfers.IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .SingleAsync(transfer => transfer.Id == dirtyTransferId);
            var originalRequest = new SyncPushRequest
            {
                DeviceId = deviceId,
                Invoices = [LocalMappings.ToDto(dirtyInvoiceBeforePull)],
                InventoryTransfers = [LocalMappings.ToDto(dirtyTransferBeforePull)]
            };
            InvokeStampOutgoingMutations(
                originalRequest,
                deviceId,
                session.SelectedBusinessDatabaseName);
            var originalInvoiceMutationId = Assert.Single(originalRequest.Invoices).MutationId;
            var originalTransferMutationId = Assert.Single(originalRequest.InventoryTransfers).MutationId;
            var originalInvoiceOutboxId = Guid.NewGuid();
            var originalTransferOutboxId = Guid.NewGuid();

            db.SyncOutboxEntries.AddRange(
                new LocalSyncOutboxEntry
                {
                    Id = originalInvoiceOutboxId,
                    MutationId = originalInvoiceMutationId,
                    DeviceId = deviceId,
                    EntityName = nameof(LocalInvoice),
                    EntityId = dirtyInvoiceId,
                    ExpectedRevision = originalRequest.Invoices[0].ExpectedRevision,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    BusinessDatabaseName = session.SelectedBusinessDatabaseName,
                    SessionId = session.SessionId,
                    UserId = session.User!.UserId,
                    Status = "Acknowledged",
                    PreparedAtUtc = oldDirtyInvoiceUpdatedAtUtc,
                    AcknowledgedAtUtc = oldDirtyInvoiceUpdatedAtUtc.AddMinutes(2)
                },
                new LocalSyncOutboxEntry
                {
                    Id = originalTransferOutboxId,
                    MutationId = originalTransferMutationId,
                    DeviceId = deviceId,
                    EntityName = nameof(LocalInventoryTransfer),
                    EntityId = dirtyTransferId,
                    ExpectedRevision = originalRequest.InventoryTransfers[0].ExpectedRevision,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    BusinessDatabaseName = session.SelectedBusinessDatabaseName,
                    SessionId = session.SessionId,
                    UserId = session.User!.UserId,
                    Status = "Acknowledged",
                    PreparedAtUtc = oldDirtyTransferUpdatedAtUtc,
                    SentAtUtc = oldDirtyTransferUpdatedAtUtc.AddMinutes(1),
                    AcknowledgedAtUtc = oldDirtyTransferUpdatedAtUtc.AddMinutes(2)
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            ItemDto CreatePulledItem(Guid id, bool isDeleted, long revision) => new()
            {
                Id = id,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "nested root item alias",
                NameMatchKey = "nested root item alias",
                SpecificationOriginal = "NESTED-ROOT-SPEC",
                SpecificationMatchKey = "NESTED-ROOT-SPEC",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                MaterialNumber = "NESTED-ROOT-MATERIAL-001",
                SerialNumber = "NESTED-ROOT-SERIAL-001",
                Revision = revision,
                IsDeleted = isDeleted,
                CreatedAtUtc = oldDirtyInvoiceUpdatedAtUtc.AddDays(-2),
                UpdatedAtUtc = oldDirtyInvoiceUpdatedAtUtc.AddHours(1)
            };

            using var sync = CreateSyncService(db, session);
            await InvokeUpsertPulledItemsAsync(
                sync,
                new SyncPullResponse
                {
                    Items =
                    [
                        CreatePulledItem(canonicalItemId, isDeleted: false, revision: 93),
                        CreatePulledItem(tombstoneItemId, isDeleted: true, revision: 94)
                    ]
                });

            db.ChangeTracker.Clear();
            var invoices = await db.Invoices.IgnoreQueryFilters()
                .Include(invoice => invoice.Lines)
                .AsNoTracking()
                .Where(invoice => invoice.Id == dirtyInvoiceId || invoice.Id == cleanInvoiceId)
                .ToDictionaryAsync(invoice => invoice.Id);
            var transfers = await db.InventoryTransfers.IgnoreQueryFilters()
                .Include(transfer => transfer.Lines)
                .AsNoTracking()
                .Where(transfer => transfer.Id == dirtyTransferId || transfer.Id == cleanTransferId)
                .ToDictionaryAsync(transfer => transfer.Id);

            Assert.All(invoices.Values, invoice =>
                Assert.Equal(canonicalItemId, Assert.Single(invoice.Lines).ItemId));
            Assert.All(transfers.Values, transfer =>
                Assert.Equal(canonicalItemId, Assert.Single(transfer.Lines).ItemId));
            Assert.True(invoices[dirtyInvoiceId].IsDirty);
            Assert.True(invoices[dirtyInvoiceId].UpdatedAtUtc > oldDirtyInvoiceUpdatedAtUtc);
            Assert.True(transfers[dirtyTransferId].IsDirty);
            Assert.True(transfers[dirtyTransferId].UpdatedAtUtc > oldDirtyTransferUpdatedAtUtc);
            Assert.False(invoices[cleanInvoiceId].IsDirty);
            Assert.Equal(oldCleanInvoiceUpdatedAtUtc, invoices[cleanInvoiceId].UpdatedAtUtc);
            Assert.False(transfers[cleanTransferId].IsDirty);
            Assert.Equal(oldCleanTransferUpdatedAtUtc, transfers[cleanTransferId].UpdatedAtUtc);

            var nextRequest = new SyncPushRequest
            {
                DeviceId = deviceId,
                Invoices = [LocalMappings.ToDto(invoices[dirtyInvoiceId])],
                InventoryTransfers = [LocalMappings.ToDto(transfers[dirtyTransferId])]
            };
            InvokeStampOutgoingMutations(
                nextRequest,
                deviceId,
                session.SelectedBusinessDatabaseName);
            var nextInvoiceMutationId = Assert.Single(nextRequest.Invoices).MutationId;
            var nextTransferMutationId = Assert.Single(nextRequest.InventoryTransfers).MutationId;
            Assert.NotEqual(originalInvoiceMutationId, nextInvoiceMutationId);
            Assert.NotEqual(originalTransferMutationId, nextTransferMutationId);

            await InvokeRecordPreparedMutationsAsync(sync, nextRequest, session);

            var outbox = await db.SyncOutboxEntries.AsNoTracking()
                .Where(entry =>
                    entry.EntityId == dirtyInvoiceId ||
                    entry.EntityId == dirtyTransferId)
                .ToListAsync();
            Assert.Equal(4, outbox.Count);
            Assert.Contains(outbox, entry =>
                entry.Id == originalInvoiceOutboxId &&
                entry.MutationId == originalInvoiceMutationId &&
                entry.Status == "Acknowledged");
            Assert.Contains(outbox, entry =>
                entry.Id == originalTransferOutboxId &&
                entry.MutationId == originalTransferMutationId &&
                entry.Status == "Acknowledged");
            Assert.Contains(outbox, entry =>
                entry.MutationId == nextInvoiceMutationId &&
                entry.Status == "Prepared");
            Assert.Contains(outbox, entry =>
                entry.MutationId == nextTransferMutationId &&
                entry.Status == "Prepared");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyPull_ItemCatalogCapabilityUpgrade_RequeuesPendingItemOnceEvenWhenPullIsEmpty()
    {
        PrepareAppRoot("georaeplan-item-catalog-capability-upgrade");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "빈 pull capability 전환 품목",
                NameMatchKey = "빈 pull capability 전환 품목",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                BoxQuantity = 10m,
                StorageLocation = "A-01",
                CatalogExtensionSyncPending = true,
                Revision = 7,
                IsDirty = false,
                CreatedAtUtc = originalUpdatedAtUtc.AddDays(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "Sync.ItemCatalogExtensionVersion",
                Value = "1"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            await InvokeApplyPullAsync(sync, new SyncPullResponse());

            db.ChangeTracker.Clear();
            var oldServerObservation = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.False(oldServerObservation.IsDirty);
            Assert.Equal(
                "0",
                await db.Settings.AsNoTracking()
                    .Where(setting => setting.Key == "Sync.ItemCatalogExtensionVersion")
                    .Select(setting => setting.Value)
                    .SingleAsync());

            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    ItemCatalogExtensionVersion = 1
                });

            db.ChangeTracker.Clear();
            var firstTransition = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.True(firstTransition.IsDirty);
            Assert.True(firstTransition.CatalogExtensionSyncPending);
            Assert.Equal(7, firstTransition.Revision);
            Assert.Equal(originalUpdatedAtUtc, firstTransition.UpdatedAtUtc);
            Assert.Equal(
                "1",
                await db.Settings.AsNoTracking()
                    .Where(setting => setting.Key == "Sync.ItemCatalogExtensionVersion")
                    .Select(setting => setting.Value)
                    .SingleAsync());

            var acknowledged = await db.Items.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == itemId);
            acknowledged.IsDirty = false;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    ItemCatalogExtensionVersion = 1
                });

            db.ChangeTracker.Clear();
            var repeatedVersion = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.False(repeatedVersion.IsDirty);
            Assert.True(repeatedVersion.CatalogExtensionSyncPending);
            Assert.Equal(7, repeatedVersion.Revision);
            Assert.Equal(originalUpdatedAtUtc, repeatedVersion.UpdatedAtUtc);

            var completedItem = await db.Items.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == itemId);
            completedItem.CatalogExtensionSyncPending = false;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await InvokeApplyPullAsync(sync, new SyncPullResponse());
            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    ItemCatalogExtensionVersion = 1
                });

            db.ChangeTracker.Clear();
            var completedAcrossCapabilityRestart = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.False(completedAcrossCapabilityRestart.IsDirty);
            Assert.False(completedAcrossCapabilityRestart.CatalogExtensionSyncPending);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UpsertPulledItems_LegacyThenUpgradedServer_PreservesAndEventuallyAcknowledgesCatalogExtensions()
    {
        PrepareAppRoot("georaeplan-item-pull-optional-fields-compatibility");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var pulledUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(5);
            var lastPurchaseDate = new DateOnly(2026, 7, 10);
            var lastSaleDate = new DateOnly(2026, 7, 18);
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "구버전 서버 pull 호환 품목",
                NameMatchKey = "구버전 서버 pull 호환 품목",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "BOX",
                BoxQuantity = 16m,
                StorageLocation = "D-04-01",
                LastPurchaseDate = lastPurchaseDate,
                LastSaleDate = lastSaleDate,
                Notes = "pull 이전",
                Revision = 10,
                IsDirty = false,
                CreatedAtUtc = originalUpdatedAtUtc.AddDays(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            await InvokeUpsertPulledItemsAsync(
                sync,
                new SyncPullResponse
                {
                    Items =
                    [
                        new ItemDto
                        {
                            Id = itemId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            NameOriginal = "구버전 서버 pull 호환 품목",
                            NameMatchKey = "구버전 서버 pull 호환 품목",
                            ItemKind = ItemKinds.Product,
                            TrackingType = ItemTrackingTypes.Stock,
                            Unit = "BOX",
                            BoxQuantity = null,
                            StorageLocation = null,
                            LastPurchaseDate = null,
                            LastSaleDate = null,
                            Notes = "pull 이후",
                            Revision = 11,
                            CreatedAtUtc = originalUpdatedAtUtc.AddDays(-1),
                            UpdatedAtUtc = pulledUpdatedAtUtc
                        }
                    ]
                });

            db.ChangeTracker.Clear();
            var stored = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.Equal(16m, stored.BoxQuantity);
            Assert.Equal("D-04-01", stored.StorageLocation);
            Assert.Equal(lastPurchaseDate, stored.LastPurchaseDate);
            Assert.Equal(lastSaleDate, stored.LastSaleDate);
            Assert.Equal("pull 이후", stored.Notes);
            Assert.Equal(11, stored.Revision);
            Assert.False(stored.IsDirty);
            Assert.True(stored.CatalogExtensionSyncPending);

            await InvokeUpsertPulledItemsAsync(
                sync,
                new SyncPullResponse
                {
                    Items =
                    [
                        new ItemDto
                        {
                            Id = itemId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            NameOriginal = "구버전 서버 pull 호환 품목",
                            NameMatchKey = "구버전 서버 pull 호환 품목",
                            ItemKind = ItemKinds.Product,
                            TrackingType = ItemTrackingTypes.Stock,
                            Unit = "BOX",
                            BoxQuantity = 0m,
                            StorageLocation = string.Empty,
                            LastPurchaseDate = null,
                            LastPurchaseDateSpecified = false,
                            LastSaleDate = null,
                            LastSaleDateSpecified = false,
                            Notes = "날짜 presence 미지정",
                            Revision = 12,
                            CreatedAtUtc = originalUpdatedAtUtc.AddDays(-1),
                            UpdatedAtUtc = pulledUpdatedAtUtc.AddMinutes(1)
                        }
                    ]
                });

            db.ChangeTracker.Clear();
            var falsePresencePreserved = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.Equal(16m, falsePresencePreserved.BoxQuantity);
            Assert.Equal("D-04-01", falsePresencePreserved.StorageLocation);
            Assert.Equal(lastPurchaseDate, falsePresencePreserved.LastPurchaseDate);
            Assert.Equal(lastSaleDate, falsePresencePreserved.LastSaleDate);
            Assert.Equal(12, falsePresencePreserved.Revision);
            Assert.False(falsePresencePreserved.IsDirty);
            Assert.True(falsePresencePreserved.CatalogExtensionSyncPending);

            await InvokeUpsertPulledItemsAsync(
                sync,
                new SyncPullResponse
                {
                    Items =
                    [
                        new ItemDto
                        {
                            Id = itemId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            NameOriginal = "구버전 서버 pull 호환 품목",
                            NameMatchKey = "구버전 서버 pull 호환 품목",
                            ItemKind = ItemKinds.Product,
                            TrackingType = ItemTrackingTypes.Stock,
                            Unit = "BOX",
                            BoxQuantity = 0m,
                            StorageLocation = string.Empty,
                            LastPurchaseDate = null,
                            LastPurchaseDateSpecified = true,
                            LastSaleDate = null,
                            LastSaleDateSpecified = true,
                            Notes = "업그레이드 서버 기본값",
                            Revision = 13,
                            CreatedAtUtc = originalUpdatedAtUtc.AddDays(-1),
                            UpdatedAtUtc = pulledUpdatedAtUtc.AddMinutes(2)
                        }
                    ]
                });

            var requeued = await db.Items.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == itemId);
            Assert.Equal(16m, requeued.BoxQuantity);
            Assert.Equal("D-04-01", requeued.StorageLocation);
            Assert.Equal(lastPurchaseDate, requeued.LastPurchaseDate);
            Assert.Equal(lastSaleDate, requeued.LastSaleDate);
            Assert.Equal(13, requeued.Revision);
            Assert.Equal(pulledUpdatedAtUtc.AddMinutes(2), requeued.UpdatedAtUtc);
            Assert.True(requeued.IsDirty);
            Assert.True(requeued.CatalogExtensionSyncPending);

            var acknowledgedUpdatedAtUtc = pulledUpdatedAtUtc.AddMinutes(3);
            db.ChangeTracker.Clear();
            db.Settings.AddRange(
                new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "13"
                },
                new LocalSetting
                {
                    Key = "Sync.ItemCatalogExtensionVersion",
                    Value = "1"
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var echo = new ItemDto
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "구버전 서버 pull 호환 품목",
                NameMatchKey = "구버전 서버 pull 호환 품목",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "BOX",
                BoxQuantity = 16m,
                StorageLocation = "D-04-01",
                LastPurchaseDate = lastPurchaseDate,
                LastPurchaseDateSpecified = true,
                LastSaleDate = lastSaleDate,
                LastSaleDateSpecified = true,
                Notes = "업그레이드 서버 기본값",
                Revision = 14,
                CreatedAtUtc = originalUpdatedAtUtc.AddDays(-1),
                UpdatedAtUtc = acknowledgedUpdatedAtUtc
            };
            var handler = new DelayedPushAckThenEmptyPullHandler(
                itemId,
                entityName: "Item",
                acceptedRevision: 14,
                acceptedUpdatedAtUtc: acknowledgedUpdatedAtUtc,
                pullResponse: new SyncPullResponse
                {
                    CurrentServerRevision = 14,
                    ItemCatalogExtensionVersion = 1,
                    Items = [echo]
                });
            using var acknowledgedSync =
                CreateSyncService(db, CreateAdminSession(), handler);
            var syncTask = acknowledgedSync.TrySyncAsync();
            var pushedRequest = await handler.PushReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(15));
            try
            {
                var pushedItem = Assert.Single(
                    pushedRequest.Items,
                    item => item.Id == itemId);
                Assert.Equal(13, pushedItem.ExpectedRevision);
                Assert.Equal(16m, pushedItem.BoxQuantity);
                Assert.Equal("D-04-01", pushedItem.StorageLocation);
                Assert.Equal(lastPurchaseDate, pushedItem.LastPurchaseDate);
                Assert.Equal(lastSaleDate, pushedItem.LastSaleDate);
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.True(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            db.ChangeTracker.Clear();
            var completed = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.Equal(16m, completed.BoxQuantity);
            Assert.Equal("D-04-01", completed.StorageLocation);
            Assert.Equal(lastPurchaseDate, completed.LastPurchaseDate);
            Assert.Equal(lastSaleDate, completed.LastSaleDate);
            Assert.Equal(14, completed.Revision);
            Assert.False(completed.IsDirty);
            Assert.False(completed.CatalogExtensionSyncPending);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UpsertPulledItems_CatalogMismatchCasMiss_PreservesConcurrentLocalEdit()
    {
        PrepareAppRoot("georaeplan-item-catalog-mismatch-cas");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.NewGuid();
            var localUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5);
            var incomingUpdatedAtUtc = localUpdatedAtUtc.AddMinutes(1);
            var concurrentUpdatedAtUtc = incomingUpdatedAtUtc.AddMinutes(1);
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "catalog mismatch CAS item",
                NameMatchKey = "catalog mismatch CAS item",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                BoxQuantity = 8m,
                StorageLocation = "LOCAL-01",
                CatalogExtensionSyncPending = true,
                Notes = "local before pull",
                Revision = 12,
                IsDirty = false,
                CreatedAtUtc = localUpdatedAtUtc.AddDays(-1),
                UpdatedAtUtc = localUpdatedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            sync.BeforePulledItemCatalogMismatchRequeueAsyncForTesting =
                async _ =>
                {
                    await using var concurrentDb = new LocalDbContext();
                    var concurrent = await concurrentDb.Items.IgnoreQueryFilters()
                        .SingleAsync(item => item.Id == itemId);
                    concurrent.StorageLocation = "USER-EDIT-99";
                    concurrent.Notes = "concurrent user edit";
                    concurrent.IsDirty = true;
                    concurrent.UpdatedAtUtc = concurrentUpdatedAtUtc;
                    await concurrentDb.SaveChangesAsync();
                };

            await InvokeUpsertPulledItemsAsync(
                sync,
                new SyncPullResponse
                {
                    Items =
                    [
                        new ItemDto
                        {
                            Id = itemId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            NameOriginal = "catalog mismatch CAS item",
                            NameMatchKey = "catalog mismatch CAS item",
                            ItemKind = ItemKinds.Product,
                            TrackingType = ItemTrackingTypes.Stock,
                            BoxQuantity = 0m,
                            StorageLocation = string.Empty,
                            LastPurchaseDateSpecified = true,
                            LastSaleDateSpecified = true,
                            Notes = "server baseline",
                            Revision = 13,
                            CreatedAtUtc = localUpdatedAtUtc.AddDays(-1),
                            UpdatedAtUtc = incomingUpdatedAtUtc
                        }
                    ]
                });

            db.ChangeTracker.Clear();
            var stored = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.Equal("USER-EDIT-99", stored.StorageLocation);
            Assert.Equal("concurrent user edit", stored.Notes);
            Assert.True(stored.IsDirty);
            Assert.True(stored.CatalogExtensionSyncPending);
            Assert.Equal(13, stored.Revision);
            Assert.Equal(concurrentUpdatedAtUtc, stored.UpdatedAtUtc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyPull_ItemCatalogCapabilityAndActiveEcho_PreserveDirtyTombstoneThenAllowCleanRestore()
    {
        PrepareAppRoot("georaeplan-item-catalog-deleted-no-resurrection");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.NewGuid();
            var deletedUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5);
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "deleted catalog item",
                NameMatchKey = "deleted catalog item",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                BoxQuantity = 4m,
                StorageLocation = "DELETED-01",
                CatalogExtensionSyncPending = false,
                Revision = 10,
                IsDeleted = true,
                IsDirty = true,
                CreatedAtUtc = deletedUpdatedAtUtc.AddDays(-1),
                UpdatedAtUtc = deletedUpdatedAtUtc
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "Sync.ItemCatalogExtensionVersion",
                Value = "0"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    ItemCatalogExtensionVersion = 1
                });

            db.ChangeTracker.Clear();
            var afterCapability = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.True(afterCapability.IsDeleted);
            Assert.True(afterCapability.IsDirty);
            Assert.False(afterCapability.CatalogExtensionSyncPending);

            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    ItemCatalogExtensionVersion = 1,
                    Items =
                    [
                        new ItemDto
                        {
                            Id = itemId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            NameOriginal = "active server echo",
                            NameMatchKey = "active server echo",
                            ItemKind = ItemKinds.Product,
                            TrackingType = ItemTrackingTypes.Stock,
                            BoxQuantity = 0m,
                            StorageLocation = string.Empty,
                            LastPurchaseDateSpecified = true,
                            LastSaleDateSpecified = true,
                            Revision = 11,
                            CreatedAtUtc = deletedUpdatedAtUtc.AddDays(-1),
                            UpdatedAtUtc = deletedUpdatedAtUtc.AddMinutes(1)
                        }
                    ]
                });

            db.ChangeTracker.Clear();
            var afterActiveEcho = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.True(afterActiveEcho.IsDeleted);
            Assert.True(afterActiveEcho.IsDirty);
            Assert.False(afterActiveEcho.CatalogExtensionSyncPending);
            Assert.Equal(10, afterActiveEcho.Revision);
            Assert.Equal("deleted catalog item", afterActiveEcho.NameOriginal);
            Assert.Equal("DELETED-01", afterActiveEcho.StorageLocation);

            var acknowledgedTombstone = await db.Items.IgnoreQueryFilters()
                .SingleAsync(item => item.Id == itemId);
            acknowledgedTombstone.IsDirty = false;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await InvokeApplyPullAsync(
                sync,
                new SyncPullResponse
                {
                    ItemCatalogExtensionVersion = 1,
                    Items =
                    [
                        new ItemDto
                        {
                            Id = itemId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            NameOriginal = "active server echo",
                            NameMatchKey = "active server echo",
                            ItemKind = ItemKinds.Product,
                            TrackingType = ItemTrackingTypes.Stock,
                            BoxQuantity = 0m,
                            StorageLocation = string.Empty,
                            LastPurchaseDateSpecified = true,
                            LastSaleDateSpecified = true,
                            Revision = 11,
                            CreatedAtUtc = deletedUpdatedAtUtc.AddDays(-1),
                            UpdatedAtUtc = deletedUpdatedAtUtc.AddMinutes(1)
                        }
                    ]
                });

            db.ChangeTracker.Clear();
            var restored = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.False(restored.IsDeleted);
            Assert.False(restored.IsDirty);
            Assert.False(restored.CatalogExtensionSyncPending);
            Assert.Equal(11, restored.Revision);
            Assert.Equal("active server echo", restored.NameOriginal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task UpsertPulledItems_SpecifiedNullOptionalDates_ClearsCleanLocalValues()
    {
        PrepareAppRoot("georaeplan-item-pull-explicit-date-clear");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "명시 날짜 삭제 pull 품목",
                NameMatchKey = "명시 날짜 삭제 pull 품목",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "BOX",
                BoxQuantity = 6m,
                StorageLocation = "E-03",
                LastPurchaseDate = new DateOnly(2026, 7, 5),
                LastSaleDate = new DateOnly(2026, 7, 8),
                Revision = 20,
                IsDirty = false,
                CreatedAtUtc = originalUpdatedAtUtc.AddDays(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "4"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, CreateAdminSession());
            await InvokeUpsertPulledItemsAsync(
                sync,
                new SyncPullResponse
                {
                    Items =
                    [
                        new ItemDto
                        {
                            Id = itemId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            NameOriginal = "명시 날짜 삭제 pull 품목",
                            NameMatchKey = "명시 날짜 삭제 pull 품목",
                            ItemKind = ItemKinds.Product,
                            TrackingType = ItemTrackingTypes.Stock,
                            Unit = "BOX",
                            BoxQuantity = 6m,
                            StorageLocation = "E-03",
                            LastPurchaseDate = null,
                            LastPurchaseDateSpecified = true,
                            LastSaleDate = null,
                            LastSaleDateSpecified = true,
                            Revision = 21,
                            CreatedAtUtc = originalUpdatedAtUtc.AddDays(-1),
                            UpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(5)
                        }
                    ]
                });

            db.ChangeTracker.Clear();
            var stored = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.Null(stored.LastPurchaseDate);
            Assert.Null(stored.LastSaleDate);
            Assert.Equal(6m, stored.BoxQuantity);
            Assert.Equal("E-03", stored.StorageLocation);
            Assert.Equal(21, stored.Revision);
            Assert.False(stored.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_DirtyLegacyItemWithoutObservedCapability_OmitsDefaultCatalogExtensionsBeforeFirstPull()
    {
        PrepareAppRoot("georaeplan-item-legacy-outbound-catalog-extension-omission");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5);
            var serverUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            var serverLastPurchaseDate = new DateOnly(2026, 8, 1);
            var serverLastSaleDate = new DateOnly(2026, 8, 2);
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "legacy outbound item",
                NameMatchKey = "LEGACYOUTBOUNDITEM",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                Notes = "ordinary legacy edit",
                Revision = 4,
                IsDirty = true,
                CatalogExtensionSyncPending = false,
                CreatedAtUtc = originalUpdatedAtUtc.AddDays(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "4"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPushAckThenEmptyPullHandler(
                itemId,
                entityName: "Item",
                acceptedRevision: 5,
                acceptedUpdatedAtUtc: originalUpdatedAtUtc.AddMinutes(1),
                pullResponse: new SyncPullResponse
                {
                    CurrentServerRevision = 6,
                    ItemCatalogExtensionVersion = 1,
                    Items =
                    [
                        new ItemDto
                        {
                            Id = itemId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            NameOriginal = "legacy outbound item",
                            NameMatchKey = "LEGACYOUTBOUNDITEM",
                            ItemKind = ItemKinds.Product,
                            TrackingType = ItemTrackingTypes.Stock,
                            Unit = "EA",
                            BoxQuantity = 24m,
                            StorageLocation = "SERVER-A-01",
                            LastPurchaseDate = serverLastPurchaseDate,
                            LastPurchaseDateSpecified = true,
                            LastSaleDate = serverLastSaleDate,
                            LastSaleDateSpecified = true,
                            Notes = "ordinary legacy edit",
                            Revision = 6,
                            CreatedAtUtc = originalUpdatedAtUtc.AddDays(-1),
                            UpdatedAtUtc = serverUpdatedAtUtc
                        }
                    ]
                });
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            var pushedRequest = await handler.PushReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(15));
            try
            {
                var pushedItem = Assert.Single(
                    pushedRequest.Items,
                    item => item.Id == itemId);
                Assert.Null(pushedItem.BoxQuantity);
                Assert.Null(pushedItem.StorageLocation);
                Assert.Null(pushedItem.LastPurchaseDate);
                Assert.Null(pushedItem.LastPurchaseDateSpecified);
                Assert.Null(pushedItem.LastSaleDate);
                Assert.Null(pushedItem.LastSaleDateSpecified);
                Assert.False(await db.Items.IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(item => item.Id == itemId)
                    .Select(item => item.CatalogExtensionSyncPending)
                    .SingleAsync());
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.True(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));
            db.ChangeTracker.Clear();
            var pulled = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.Equal(24m, pulled.BoxQuantity);
            Assert.Equal("SERVER-A-01", pulled.StorageLocation);
            Assert.Equal(serverLastPurchaseDate, pulled.LastPurchaseDate);
            Assert.Equal(serverLastSaleDate, pulled.LastSaleDate);
            Assert.Equal(6, pulled.Revision);
            Assert.False(pulled.IsDirty);
            Assert.False(pulled.CatalogExtensionSyncPending);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_DirtyOutboundItem_MarksCatalogExtensionPendingBeforePush()
    {
        PrepareAppRoot("georaeplan-item-outbound-catalog-extension-pending");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5);
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "outbound pending boundary item",
                NameMatchKey = "outbound pending boundary item",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                Revision = 4,
                IsDirty = true,
                CatalogExtensionSyncPending = false,
                CreatedAtUtc = originalUpdatedAtUtc.AddDays(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            db.Settings.AddRange(
                new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "4"
                },
                new LocalSetting
                {
                    Key = "Sync.ItemCatalogExtensionVersion",
                    Value = "1"
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPushAckThenEmptyPullHandler(
                itemId,
                entityName: "Item",
                acceptedRevision: 5,
                acceptedUpdatedAtUtc: originalUpdatedAtUtc.AddMinutes(1),
                pullResponse: new SyncPullResponse
                {
                    CurrentServerRevision = 5,
                    ItemCatalogExtensionVersion = 1
                });
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            var pushedRequest = await handler.PushReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(15));
            try
            {
                var pushedItem = Assert.Single(
                    pushedRequest.Items,
                    item => item.Id == itemId);
                Assert.Equal(4, pushedItem.ExpectedRevision);
                Assert.True(await db.Items.IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(item => item.Id == itemId)
                    .Select(item => item.CatalogExtensionSyncPending)
                    .SingleAsync());
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.True(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));
            db.ChangeTracker.Clear();
            var acknowledgedWithoutEcho = await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(item => item.Id == itemId);
            Assert.False(acknowledgedWithoutEcho.IsDirty);
            Assert.True(acknowledgedWithoutEcho.CatalogExtensionSyncPending);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_DelayedPushAck_PreservesNewerConcurrentUnitEditWhileAdvancingRevision()
    {
        PrepareAppRoot("georaeplan-delayed-unit-ack-race");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var acceptedUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(1);
            var newerUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const long originalRevision = 3;
            const long acceptedRevision = 4;
            const string originalName = "ACK 경합 이전 단위";
            const string newerName = "ACK 대기 중 수정한 단위";

            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = originalName,
                IsActive = true,
                Revision = originalRevision,
                IsDirty = true,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPushAckThenEmptyPullHandler(
                unitId,
                entityName: "Unit",
                acceptedRevision,
                acceptedUpdatedAtUtc);
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            var pushedRequest = await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            UnitDto pushedUnit;
            try
            {
                pushedUnit = Assert.Single(pushedRequest.Units, unit => unit.Id == unitId);
                Assert.Equal(originalName, pushedUnit.Name);
                Assert.Equal(originalRevision, pushedUnit.ExpectedRevision);

                await using var concurrentDb = new LocalDbContext();
                var concurrentlyEdited = await concurrentDb.Units.IgnoreQueryFilters()
                    .SingleAsync(unit => unit.Id == unitId);
                concurrentlyEdited.Name = newerName;
                concurrentlyEdited.UpdatedAtUtc = newerUpdatedAtUtc;
                concurrentlyEdited.IsDirty = true;
                await concurrentDb.SaveChangesAsync();
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.True(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            db.ChangeTracker.Clear();
            var saved = await db.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(newerName, saved.Name);
            Assert.Equal(newerUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.True(saved.IsDirty);
            Assert.Equal(acceptedRevision, saved.Revision);

            var outbox = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry =>
                    entry.EntityName == nameof(LocalUnit) &&
                    entry.EntityId == unitId);
            Assert.Equal("Acknowledged", outbox.Status);
            Assert.Equal(pushedUnit.MutationId, outbox.MutationId);
            Assert.Equal(1, handler.PushCount);
            Assert.True(handler.PullCount >= 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_DelayedPushAck_AppliesAssignedInvoiceNumberWithoutCleaningNewerConcurrentEdit()
    {
        PrepareAppRoot("georaeplan-delayed-invoice-number-ack-race");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var acceptedUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(1);
            var newerUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const long originalRevision = 8;
            const long acceptedRevision = 9;
            const string assignedInvoiceNumber = "S-ACK-RACE-0001";
            const string originalMemo = "ACK 경합 이전 메모";
            const string newerMemo = "ACK 대기 중 수정한 메모";

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "ACK 경합 거래처",
                NameMatchKey = "ACK 경합 거래처",
                Revision = 1,
                IsDirty = false,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-2),
                UpdatedAtUtc = originalUpdatedAtUtc.AddHours(-1)
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = string.Empty,
                LocalTempNumber = "LOCAL-ACK-RACE-0001",
                VoucherType = VoucherType.Sales,
                InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
                Memo = originalMemo,
                Revision = originalRevision,
                IsDirty = true,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPushAckThenEmptyPullHandler(
                invoiceId,
                entityName: "Invoice",
                acceptedRevision,
                acceptedUpdatedAtUtc,
                assignedInvoiceNumber);
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            var pushedRequest = await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            InvoiceDto pushedInvoice;
            try
            {
                pushedInvoice = Assert.Single(pushedRequest.Invoices, invoice => invoice.Id == invoiceId);
                Assert.Equal(originalMemo, pushedInvoice.Memo);
                Assert.Equal(originalRevision, pushedInvoice.ExpectedRevision);

                await using var concurrentDb = new LocalDbContext();
                var concurrentlyEdited = await concurrentDb.Invoices.IgnoreQueryFilters()
                    .SingleAsync(invoice => invoice.Id == invoiceId);
                concurrentlyEdited.Memo = newerMemo;
                concurrentlyEdited.UpdatedAtUtc = newerUpdatedAtUtc;
                concurrentlyEdited.IsDirty = true;
                await concurrentDb.SaveChangesAsync();
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.True(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            db.ChangeTracker.Clear();
            var saved = await db.Invoices.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(invoice => invoice.Id == invoiceId);
            Assert.Equal(assignedInvoiceNumber, saved.InvoiceNumber);
            Assert.Equal(newerMemo, saved.Memo);
            Assert.Equal(newerUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.True(saved.IsDirty);
            Assert.Equal(acceptedRevision, saved.Revision);

            var outbox = await db.SyncOutboxEntries.AsNoTracking()
                .SingleAsync(entry =>
                    entry.EntityName == nameof(LocalInvoice) &&
                    entry.EntityId == invoiceId);
            Assert.Equal("Acknowledged", outbox.Status);
            Assert.Equal(pushedInvoice.MutationId, outbox.MutationId);
            Assert.Equal(1, handler.PushCount);
            Assert.True(handler.PullCount >= 1);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_DelayedPushAck_PreservesUnsavedTrackedUnitEditForRevisionRebaseAndLaterSave()
    {
        PrepareAppRoot("georaeplan-delayed-tracked-unit-ack-race");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var acceptedUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(1);
            var newerUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const long originalRevision = 31;
            const long acceptedRevision = 32;
            const string originalName = "tracked ACK 이전 단위";
            const string newerName = "tracked ACK 대기 중 수정한 단위";

            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = originalName,
                IsActive = true,
                Revision = originalRevision,
                IsDirty = true,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPushAckThenEmptyPullHandler(
                unitId,
                entityName: "Unit",
                acceptedRevision,
                acceptedUpdatedAtUtc);
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            var pushedRequest = await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var pushedUnit = Assert.Single(pushedRequest.Units, unit => unit.Id == unitId);
            Assert.Equal(originalName, pushedUnit.Name);
            Assert.Equal(originalRevision, pushedUnit.ExpectedRevision);

            LocalUnit tracked;
            try
            {
                tracked = await db.Units.IgnoreQueryFilters()
                    .SingleAsync(unit => unit.Id == unitId);
                tracked.Name = newerName;
                tracked.UpdatedAtUtc = newerUpdatedAtUtc;
                tracked.IsDirty = true;
                db.ChangeTracker.DetectChanges();
                Assert.Equal(EntityState.Modified, db.Entry(tracked).State);
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.True(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(newerName, tracked.Name);
            Assert.Equal(newerUpdatedAtUtc, tracked.UpdatedAtUtc);
            Assert.True(tracked.IsDirty);
            Assert.Equal(acceptedRevision, tracked.Revision);
            Assert.Equal(EntityState.Modified, db.Entry(tracked).State);

            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            var saved = await db.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(newerName, saved.Name);
            Assert.Equal(newerUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.True(saved.IsDirty);
            Assert.Equal(acceptedRevision, saved.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_PreexistingModifiedCustomer_IsNotCommittedBeforeDelayedPushAck()
    {
        PrepareAppRoot("georaeplan-delayed-non-request-customer-ack");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var acceptedUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(1);
            var customerUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const long originalRevision = 35;
            const long acceptedRevision = 36;
            const string originalCustomerName = "non-request customer before sync";
            const string modifiedCustomerName = "non-request customer modified before sync";

            db.Units.Add(new LocalUnit
                {
                    Id = unitId,
                    Name = "prepared unit",
                    IsActive = true,
                    Revision = originalRevision,
                    IsDirty = true,
                    CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                    UpdatedAtUtc = originalUpdatedAtUtc
                });
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = originalCustomerName,
                NameMatchKey = originalCustomerName,
                Revision = 7,
                IsDirty = false,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = customerUpdatedAtUtc
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "5"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var trackedCustomer = await db.Customers.IgnoreQueryFilters()
                .SingleAsync(customer => customer.Id == customerId);
            trackedCustomer.NameOriginal = modifiedCustomerName;
            db.ChangeTracker.DetectChanges();
            Assert.False(trackedCustomer.IsDirty);
            Assert.Equal(EntityState.Modified, db.Entry(trackedCustomer).State);

            var handler = new DelayedPushAckThenEmptyPullHandler(
                unitId,
                entityName: "Unit",
                acceptedRevision,
                acceptedUpdatedAtUtc);
            using var sync = CreateSyncService(db, session, handler);
            var lastStatus = string.Empty;
            sync.SyncStatusChanged += status => lastStatus = status;

            var syncTask = sync.TrySyncAsync();
            try
            {
                var pushedRequest = await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Single(pushedRequest.Units, unit => unit.Id == unitId);
                Assert.DoesNotContain(pushedRequest.Customers, customer => customer.Id == customerId);

                await using var beforeExplicitSaveDb = new LocalDbContext();
                var persistedBeforeExplicitSave = await beforeExplicitSaveDb.Customers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(customer => customer.Id == customerId);
                Assert.Equal(originalCustomerName, persistedBeforeExplicitSave.NameOriginal);
                Assert.False(persistedBeforeExplicitSave.IsDirty);
            }
            finally
            {
                handler.ReleasePush();
            }

            var succeeded = await syncTask.WaitAsync(TimeSpan.FromSeconds(15));
            var lastError = await db.Settings.AsNoTracking()
                .Where(setting => setting.Key == "Sync.LastError")
                .Select(setting => setting.Value)
                .FirstOrDefaultAsync();
            Assert.True(
                succeeded,
                $"{lastStatus} {lastError}");

            Assert.Equal(modifiedCustomerName, trackedCustomer.NameOriginal);
            Assert.Equal(originalCustomerName, trackedCustomer.NameMatchKey);
            Assert.True(trackedCustomer.IsDirty);
            Assert.Equal(7, trackedCustomer.Revision);
            Assert.Equal(EntityState.Modified, db.Entry(trackedCustomer).State);

            await db.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.Customers.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(customer => customer.Id == customerId);
            Assert.Equal(modifiedCustomerName, saved.NameOriginal);
            Assert.Equal(originalCustomerName, saved.NameMatchKey);
            Assert.True(saved.IsDirty);
            Assert.Equal(7, saved.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TrySyncScopeAsync_PreexistingModifiedCustomer_IsRestoredAfterDelayedPushResult(
        bool throwPush)
    {
        PrepareAppRoot("georaeplan-scope-preexisting-customer-ack");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-10);
            const string originalCustomerName = "scope customer before sync";
            const string modifiedCustomerName = "scope customer modified before sync";

            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = "scope prepared unit",
                IsActive = true,
                Revision = 43,
                IsDirty = true,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = originalCustomerName,
                NameMatchKey = originalCustomerName,
                Revision = 9,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var trackedCustomer = await db.Customers.IgnoreQueryFilters()
                .SingleAsync(customer => customer.Id == customerId);
            trackedCustomer.NameOriginal = modifiedCustomerName;
            db.ChangeTracker.DetectChanges();
            Assert.False(trackedCustomer.IsDirty);
            Assert.Equal(EntityState.Modified, db.Entry(trackedCustomer).State);

            var handler = new DelayedPushAckThenEmptyPullHandler(
                unitId,
                entityName: "Unit",
                acceptedRevision: 44,
                acceptedUpdatedAtUtc: now.AddMinutes(1),
                pushException: throwPush
                    ? new InvalidOperationException("simulated scoped push failure")
                    : null);
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncScopeAsync("SHARED");
            try
            {
                var pushedRequest = await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Single(pushedRequest.Units, unit => unit.Id == unitId);
                Assert.DoesNotContain(pushedRequest.Customers, customer => customer.Id == customerId);

                await using var beforeExplicitSaveDb = new LocalDbContext();
                var persistedBeforeExplicitSave = await beforeExplicitSaveDb.Customers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(customer => customer.Id == customerId);
                Assert.Equal(originalCustomerName, persistedBeforeExplicitSave.NameOriginal);
                Assert.False(persistedBeforeExplicitSave.IsDirty);
            }
            finally
            {
                handler.ReleasePush();
            }

            var result = await syncTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(!throwPush, result.Succeeded);
            Assert.Equal(modifiedCustomerName, trackedCustomer.NameOriginal);
            Assert.True(trackedCustomer.IsDirty);
            Assert.Equal(EntityState.Modified, db.Entry(trackedCustomer).State);

            await db.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.Customers.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(customer => customer.Id == customerId);
            Assert.Equal(modifiedCustomerName, saved.NameOriginal);
            Assert.True(saved.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RefreshSharedMirrorFromServerAsync_OfficeScopePreservesForeignScopeRows()
    {
        PrepareAppRoot("georaeplan-refresh-office-scope-preserves-foreign");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var foreignCustomerId = Guid.NewGuid();
            var ownCustomerId = Guid.NewGuid();
            var sharedItemId = Guid.NewGuid();
            var foreignOwnedItemId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            db.Customers.Add(
                new LocalCustomer
                {
                    Id = foreignCustomerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "foreign scope customer",
                    NameMatchKey = "FOREIGNSCOPECUSTOMER",
                    Revision = 4,
                    IsDirty = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            db.Items.AddRange(
                new LocalItem
                {
                    Id = sharedItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "shared stock item",
                    NameMatchKey = "SHAREDSTOCKITEM",
                    Unit = "EA",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    CurrentStock = 10m,
                    Revision = 4,
                    IsDirty = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                },
                new LocalItem
                {
                    Id = foreignOwnedItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "foreign owned item with own warehouse stock",
                    NameMatchKey = "FOREIGNOWNEDITEMWITHOWNWAREHOUSESTOCK",
                    Unit = "EA",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    CurrentStock = 3m,
                    Revision = 7,
                    IsDirty = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                });
            db.ItemWarehouseStocks.AddRange(
                new LocalItemWarehouseStock
                {
                    ItemId = sharedItemId,
                    WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    Quantity = 4m,
                    Revision = 11,
                    UpdatedAtUtc = now
                },
                new LocalItemWarehouseStock
                {
                    ItemId = sharedItemId,
                    WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 6m,
                    Revision = 9,
                    UpdatedAtUtc = now
                },
                new LocalItemWarehouseStock
                {
                    ItemId = foreignOwnedItemId,
                    WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                    Quantity = 3m,
                    Revision = 8,
                    UpdatedAtUtc = now
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var incomingOwnCustomer = new LocalCustomer
            {
                Id = ownCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                NameOriginal = "own scope customer",
                NameMatchKey = "OWNSCOPECUSTOMER",
                Revision = 12,
                IsDirty = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now.AddMinutes(1)
            };
            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 12,
                    Customers = [LocalMappings.ToDto(incomingOwnCustomer)],
                    ItemWarehouseStocks =
                    [
                        LocalMappings.ToDto(new LocalItemWarehouseStock
                        {
                            ItemId = sharedItemId,
                            WarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                            Quantity = 4m,
                            Revision = 12,
                            UpdatedAtUtc = now.AddMinutes(1)
                        })
                    ]
                });
            var session = CreateOnlineOfficeSession(
                TenantScopeCatalog.UsenetGroup,
                OfficeCodeCatalog.Yeonsu);
            using var sync = CreateSyncService(db, session, handler);
            var sharedResetAttempted = false;
            sync.BeforeSharedMirrorResetAsyncForTesting = _ =>
            {
                sharedResetAttempted = true;
                return Task.CompletedTask;
            };

            var refreshTask = sync.RefreshSharedMirrorFromServerAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.ReleasePull();

            Assert.True(await refreshTask.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.False(sharedResetAttempted);

            db.ChangeTracker.Clear();
            Assert.NotNull(await db.Customers.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(customer => customer.Id == foreignCustomerId));
            Assert.NotNull(await db.Customers.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(customer => customer.Id == ownCustomerId));
            Assert.Equal(6m, await db.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock =>
                    stock.ItemId == sharedItemId &&
                    stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(9L, await db.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock =>
                    stock.ItemId == sharedItemId &&
                    stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
                .Select(stock => stock.Revision)
                .SingleAsync());
            Assert.Equal(10m, await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => item.Id == sharedItemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());
            Assert.Equal(3m, await db.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock =>
                    stock.ItemId == foreignOwnedItemId &&
                    stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
                .Select(stock => stock.Quantity)
                .SingleAsync());
            Assert.Equal(8L, await db.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock =>
                    stock.ItemId == foreignOwnedItemId &&
                    stock.WarehouseCode == OfficeCodeCatalog.YeonsuMainWarehouse)
                .Select(stock => stock.Revision)
                .SingleAsync());
            Assert.Equal(3m, await db.Items.IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => item.Id == foreignOwnedItemId)
                .Select(item => item.CurrentStock)
                .SingleAsync());
            Assert.Equal("12", await db.Settings
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
    public async Task RefreshSharedMirrorFromServerAsync_PreexistingTrackedEdit_BlocksBeforePull()
    {
        PrepareAppRoot("georaeplan-refresh-preexisting-edit-block");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            const string originalName = "refresh customer before edit";
            const string modifiedName = "refresh customer unsaved edit";
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = originalName,
                NameMatchKey = originalName,
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var trackedCustomer = await db.Customers.IgnoreQueryFilters()
                .SingleAsync(customer => customer.Id == customerId);
            trackedCustomer.NameOriginal = modifiedName;
            db.ChangeTracker.DetectChanges();
            Assert.Equal(EntityState.Modified, db.Entry(trackedCustomer).State);

            var handler = new DelayedPushAckThenEmptyPullHandler(
                Guid.NewGuid(),
                entityName: "Unit",
                acceptedRevision: 1,
                acceptedUpdatedAtUtc: now);
            using var sync = CreateSyncService(db, CreateAdminSession(), handler);

            Assert.False(await sync.RefreshSharedMirrorFromServerAsync());
            Assert.Equal(0, handler.PullCount);
            Assert.Equal(EntityState.Modified, db.Entry(trackedCustomer).State);

            await using var verificationDb = new LocalDbContext();
            var persisted = await verificationDb.Customers.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(customer => customer.Id == customerId);
            Assert.Equal(originalName, persisted.NameOriginal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshSharedMirrorFromServerAsync_EditDuringPull_IsRestoredWithoutApplyingMirror(
        bool throwPull)
    {
        PrepareAppRoot($"georaeplan-refresh-edit-during-pull-{throwPull}");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            const string originalName = "refresh delayed customer before edit";
            const string modifiedName = "refresh delayed customer unsaved edit";
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = originalName,
                NameMatchKey = originalName,
                Revision = 4,
                IsDirty = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(
                throwPull
                    ? new InvalidOperationException("simulated delayed pull failure")
                    : null);
            using var sync = CreateSyncService(db, CreateAdminSession(), handler);

            var refreshTask = sync.RefreshSharedMirrorFromServerAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var trackedCustomer = await db.Customers.IgnoreQueryFilters()
                .SingleAsync(customer => customer.Id == customerId);
            trackedCustomer.NameOriginal = modifiedName;
            db.ChangeTracker.DetectChanges();
            Assert.Equal(EntityState.Modified, db.Entry(trackedCustomer).State);
            handler.ReleasePull();

            Assert.False(await refreshTask.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(1, handler.PullCount);
            Assert.Equal(modifiedName, trackedCustomer.NameOriginal);
            Assert.Equal(EntityState.Modified, db.Entry(trackedCustomer).State);

            await using (var beforeExplicitSaveDb = new LocalDbContext())
            {
                var persistedBeforeExplicitSave = await beforeExplicitSaveDb.Customers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(customer => customer.Id == customerId);
                Assert.Equal(originalName, persistedBeforeExplicitSave.NameOriginal);
            }

            await db.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.Customers.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(customer => customer.Id == customerId);
            Assert.Equal(modifiedName, saved.NameOriginal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RefreshSharedMirrorFromServerAsync_DirtySaveDuringPull_DoesNotApplyMirror()
    {
        PrepareAppRoot("georaeplan-refresh-dirty-save-during-pull");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            const string modifiedName = "refresh customer saved during pull";
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "refresh customer before saved edit",
                NameMatchKey = "refresh customer before saved edit",
                Revision = 6,
                IsDirty = false,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler();
            using var sync = CreateSyncService(db, CreateAdminSession(), handler);

            var refreshTask = sync.RefreshSharedMirrorFromServerAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var trackedCustomer = await db.Customers.IgnoreQueryFilters()
                .SingleAsync(customer => customer.Id == customerId);
            trackedCustomer.NameOriginal = modifiedName;
            trackedCustomer.IsDirty = true;
            await db.SaveChangesAsync();
            handler.ReleasePull();

            Assert.False(await refreshTask.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(1, handler.PullCount);

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.Customers.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(customer => customer.Id == customerId);
            Assert.Equal(modifiedName, saved.NameOriginal);
            Assert.True(saved.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RefreshSharedMirrorFromServerAsync_BusinessDatabaseChangesDuringPull_DoesNotApplyOldOwnerResponse()
    {
        PrepareAppRoot("georaeplan-refresh-business-owner-change-during-pull");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var originalUnitId = Guid.NewGuid();
            var incomingUnitId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            db.Units.Add(new LocalUnit
            {
                Id = originalUnitId,
                Name = "기존 업체 단위",
                IsActive = true,
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 8,
                    Units =
                    [
                        new UnitDto
                        {
                            Id = incomingUnitId,
                            Name = "이전 업체 응답 단위",
                            IsActive = true,
                            Revision = 8,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now.AddMinutes(1)
                        }
                    ]
                });
            using var sync = CreateSyncService(db, session, handler);

            var refreshTask = sync.RefreshSharedMirrorFromServerAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            session.SetBusinessDatabase(TenantScopeCatalog.Itworld);
            handler.ReleasePull();

            Assert.False(await refreshTask.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(
                TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld),
                session.SelectedBusinessDatabaseName);

            await using var verificationDb = new LocalDbContext();
            Assert.NotNull(await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(unit => unit.Id == originalUnitId));
            Assert.Null(await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(unit => unit.Id == incomingUnitId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RefreshCurrentBusinessScopeFromServerAsync_BusinessDatabaseChangesDuringPull_DoesNotApplyOldOwnerResponse()
    {
        PrepareAppRoot("georaeplan-scoped-refresh-business-owner-change-during-pull");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var incomingUnitId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 9,
                    Units =
                    [
                        new UnitDto
                        {
                            Id = incomingUnitId,
                            Name = "이전 업체 범위 응답 단위",
                            IsActive = true,
                            Revision = 9,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now
                        }
                    ]
                });
            using var sync = CreateSyncService(db, session, handler);

            var refreshTask = sync.RefreshCurrentBusinessScopeFromServerAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            session.SetBusinessDatabase(TenantScopeCatalog.Itworld);
            handler.ReleasePull();

            Assert.False(await refreshTask.WaitAsync(TimeSpan.FromSeconds(15)));

            await using var verificationDb = new LocalDbContext();
            Assert.Null(await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(unit => unit.Id == incomingUnitId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ReplaceCurrentBusinessScopeCacheFromServerAsync_SuccessCommitsOnlyTargetSnapshot()
    {
        PrepareAppRoot("georaeplan-business-cache-replacement-success");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var originalUnitId = Guid.NewGuid();
            var incomingUnitId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            db.Units.Add(new LocalUnit
            {
                Id = originalUnitId,
                Name = "기존 업체 단위",
                IsActive = true,
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "InvoiceFilter.From",
                Value = "2026-08-01"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 11,
                    Units =
                    [
                        new UnitDto
                        {
                            Id = incomingUnitId,
                            Name = "대상 업체 단위",
                            IsActive = true,
                            Revision = 11,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now
                        }
                    ]
                });
            using var sync = CreateSyncService(db, session, handler);

            var replacementTask =
                sync.ReplaceCurrentBusinessScopeCacheFromServerAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.ReleasePull();

            Assert.True(await replacementTask.WaitAsync(TimeSpan.FromSeconds(15)));

            await using var verificationDb = new LocalDbContext();
            Assert.Null(await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(unit => unit.Id == originalUnitId));
            Assert.NotNull(await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(unit => unit.Id == incomingUnitId));
            Assert.Null(await verificationDb.Settings.AsNoTracking()
                .SingleOrDefaultAsync(setting => setting.Key == "InvoiceFilter.From"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ReplaceCurrentBusinessScopeCacheFromServerAsync_OwnerChangesDuringPull_PreservesPreviousCache()
    {
        PrepareAppRoot("georaeplan-business-cache-replacement-owner-change");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var originalUnitId = Guid.NewGuid();
            var incomingUnitId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            db.Units.Add(new LocalUnit
            {
                Id = originalUnitId,
                Name = "보존할 기존 업체 단위",
                IsActive = true,
                Revision = 3,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 12,
                    Units =
                    [
                        new UnitDto
                        {
                            Id = incomingUnitId,
                            Name = "폐기할 이전 범위 응답 단위",
                            IsActive = true,
                            Revision = 12,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now
                        }
                    ]
                });
            using var sync = CreateSyncService(db, session, handler);

            var replacementTask =
                sync.ReplaceCurrentBusinessScopeCacheFromServerAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            session.SetBusinessDatabase(TenantScopeCatalog.Itworld);
            handler.ReleasePull();

            Assert.False(await replacementTask.WaitAsync(TimeSpan.FromSeconds(15)));

            await using var verificationDb = new LocalDbContext();
            Assert.NotNull(await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(unit => unit.Id == originalUnitId));
            Assert.Null(await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(unit => unit.Id == incomingUnitId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TrySyncAsync_BusinessDatabaseAbaDuringApply_DiscardsResponseAndPreservesCursor()
    {
        PrepareAppRoot("georaeplan-incremental-pull-business-owner-aba");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var incomingUnitId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 2,
                    Units =
                    [
                        new UnitDto
                        {
                            Id = incomingUnitId,
                            Name = "ABA 범위 응답 단위",
                            IsActive = true,
                            Revision = 2,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now
                        }
                    ]
                });
            using var sync = CreateSyncService(db, session, handler);
            sync.AfterPulledPurgeRecordsAsyncForTesting = _ =>
            {
                session.SetBusinessDatabase(TenantScopeCatalog.Itworld);
                session.SetBusinessDatabase(TenantScopeCatalog.UsenetGroup);
                return Task.CompletedTask;
            };

            var syncTask = sync.TrySyncAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.ReleasePull();

            Assert.True(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            await using var verificationDb = new LocalDbContext();
            Assert.Null(await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(unit => unit.Id == incomingUnitId));
            Assert.Equal(
                "1",
                await verificationDb.Settings
                    .AsNoTracking()
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
    public async Task EnsureAdministrativeBusinessCachesAsync_LogoutDuringPull_DiscardsPriorSessionResponse()
    {
        PrepareAppRoot("georaeplan-admin-cache-logout-during-pull");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var incomingUnitId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 3,
                    Units =
                    [
                        new UnitDto
                        {
                            Id = incomingUnitId,
                            Name = "로그아웃 전 관리자 캐시 응답",
                            IsActive = true,
                            Revision = 3,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now
                        }
                    ]
                });
            using var sync = CreateSyncService(db, session, handler);

            var refreshTask = sync.EnsureAdministrativeBusinessCachesAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            session.Clear();
            handler.ReleasePull();

            Assert.False(await refreshTask.WaitAsync(TimeSpan.FromSeconds(15)));

            await using var verificationDb = new LocalDbContext();
            Assert.Null(await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(unit => unit.Id == incomingUnitId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TrySyncAsync_InvalidAttachmentAfterUnit_RollsBackEntirePullAndCursor()
    {
        PrepareAppRoot("georaeplan-incremental-pull-attachment-atomicity");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var incomingUnitId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 2,
                    Units =
                    [
                        new UnitDto
                        {
                            Id = incomingUnitId,
                            Name = "부분 반영되면 안 되는 단위",
                            IsActive = true,
                            Revision = 2,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now
                        }
                    ],
                    TransactionAttachments =
                    [
                        new TransactionAttachmentDto
                        {
                            Id = Guid.NewGuid(),
                            TransactionId = Guid.NewGuid(),
                            FileName = "invalid-empty-content.pdf",
                            MimeType = "application/pdf",
                            FileSize = 1,
                            FileHash = new string('0', 64),
                            FileContent = [],
                            Revision = 2,
                            CreatedAtUtc = now.AddHours(-1),
                            UpdatedAtUtc = now
                        }
                    ]
                });
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.ReleasePull();

            Assert.False(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            await using var verificationDb = new LocalDbContext();
            Assert.Null(await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(unit => unit.Id == incomingUnitId));
            var savedRevision = await verificationDb.Settings
                .AsNoTracking()
                .Where(setting => setting.Key == "LastSyncRevision")
                .Select(setting => setting.Value)
                .SingleAsync();
            Assert.Equal("1", savedRevision);
            Assert.Empty(await verificationDb.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ToListAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TrySyncAsync_DeferredInvoicePurgePersistsReceiptAndRetriesAfterCursorAdvances()
    {
        PrepareAppRoot("georaeplan-deferred-invoice-purge-cursor");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var purgedInvoiceId = Guid.NewGuid();
            var activeInvoiceId = Guid.NewGuid();
            var versionGroupId = Guid.NewGuid();
            var blockingOutboxId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Deferred purge cursor customer",
                NameMatchKey = "DEFERREDPURGECURSORCUSTOMER",
                Revision = 1,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Invoices.AddRange(
                new LocalInvoice
                {
                    Id = purgedInvoiceId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 7, 30),
                    VersionGroupId = versionGroupId,
                    VersionNumber = 1,
                    IsLatestVersion = false,
                    IsDeleted = true,
                    Revision = 1,
                    IsDirty = false,
                    CreatedAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now
                },
                new LocalInvoice
                {
                    Id = activeInvoiceId,
                    CustomerId = customerId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate = new DateOnly(2026, 7, 31),
                    VersionGroupId = versionGroupId,
                    VersionNumber = 2,
                    PreviousVersionId = purgedInvoiceId,
                    IsLatestVersion = true,
                    IsDeleted = false,
                    Revision = 2,
                    IsDirty = false,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now.AddMinutes(1)
                });
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            db.SyncOutboxEntries.Add(
                new LocalSyncOutboxEntry
                {
                    Id = blockingOutboxId,
                    MutationId =
                        $"deferred-purge-block:{activeInvoiceId:N}",
                    DeviceId = "deferred-purge-test-device",
                    EntityName = nameof(LocalInvoice),
                    EntityId = activeInvoiceId,
                    ExpectedRevision = 2,
                    TenantCode =
                        TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode =
                        OfficeCodeCatalog.Usenet,
                    BusinessDatabaseName = "USENET",
                    SessionId = session.SessionId,
                    UserId = session.User!.UserId,
                    Status = "Failed",
                    ErrorMessage =
                        "test pending invoice mutation",
                    PreparedAtUtc = now.AddMinutes(1)
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var purgeReceiptId = Guid.NewGuid();
            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 3,
                    PurgeRecords =
                    [
                        new RecycleBinPurgeRecordDto
                        {
                            Id = purgeReceiptId,
                            Kind = "invoice",
                            EntityId = purgedInvoiceId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            PurgedAtUtc = now.AddMinutes(2),
                            Revision = 2,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now.AddMinutes(2)
                        }
                    ]
                },
                subsequentResponse: new SyncPullResponse
                {
                    CurrentServerRevision = 3
                });
            using (var firstSync =
                   CreateSyncService(db, session, handler))
            {
                var syncTask = firstSync.TrySyncAsync();
                await handler.PullReceived.Task.WaitAsync(
                    TimeSpan.FromSeconds(15));
                handler.ReleasePull();
                Assert.True(
                    await syncTask.WaitAsync(
                        TimeSpan.FromSeconds(15)));
            }

            await using var verificationDb = new LocalDbContext();
            Assert.True(await verificationDb.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == purgedInvoiceId));
            Assert.True(await verificationDb.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == activeInvoiceId));
            Assert.Equal(
                "3",
                await verificationDb.Settings
                    .AsNoTracking()
                    .Where(setting => setting.Key == "LastSyncRevision")
                    .Select(setting => setting.Value)
                    .SingleAsync());
            var deferred = await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .SingleAsync();
            Assert.Equal(purgeReceiptId, deferred.Id);
            Assert.Equal("USENET", deferred.BusinessDatabaseName);
            Assert.Equal(
                TenantScopeCatalog.UsenetGroup,
                deferred.TenantCode,
                ignoreCase: true);

            await verificationDb.Invoices
                .IgnoreQueryFilters()
                .Where(current =>
                    current.Id == activeInvoiceId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        current => current.IsDeleted,
                        true)
                    .SetProperty(
                        current => current.IsDirty,
                        false));
            await verificationDb.SyncOutboxEntries
                .Where(current =>
                    current.Id == blockingOutboxId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(
                        current => current.Status,
                        "Acknowledged")
                    .SetProperty(
                        current => current.ErrorMessage,
                        string.Empty)
                    .SetProperty(
                        current => current.AcknowledgedAtUtc,
                        DateTime.UtcNow));
            verificationDb.ChangeTracker.Clear();

            await using var retryDb = new LocalDbContext();
            using (var retrySync =
                   CreateSyncService(
                       retryDb,
                       session,
                       handler))
            {
                Assert.True(
                    await retrySync.TrySyncAsync()
                        .WaitAsync(TimeSpan.FromSeconds(15)));
            }

            await using var finalDb = new LocalDbContext();
            Assert.False(await finalDb.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == purgedInvoiceId ||
                    current.Id == activeInvoiceId));
            Assert.Empty(await finalDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .ToListAsync());
            Assert.Equal(
                "3",
                await finalDb.Settings
                    .AsNoTracking()
                    .Where(setting =>
                        setting.Key == "LastSyncRevision")
                    .Select(setting => setting.Value)
                    .SingleAsync());
            Assert.Equal(2, handler.PullCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TrySyncAsync_DeferredPurgeRetiresSupersededAndRetainsUnsupportedWithinOwnerScope()
    {
        PrepareAppRoot(
            "georaeplan-deferred-purge-fail-closed");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var itemReceiptId = Guid.NewGuid();
            var unknownReceiptId = Guid.NewGuid();
            var foreignReceiptId = Guid.NewGuid();
            var pendingOutboxId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode =
                    TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "newer pending purge item",
                NameMatchKey = "NEWERPENDINGPURGEITEM",
                Unit = "EA",
                Revision = 5,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.SyncOutboxEntries.Add(
                new LocalSyncOutboxEntry
                {
                    Id = pendingOutboxId,
                    MutationId =
                        $"pending-item:{itemId:N}",
                    DeviceId = "deferred-purge-test-device",
                    EntityName = nameof(LocalItem),
                    EntityId = itemId,
                    ExpectedRevision = 5,
                    TenantCode =
                        TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode =
                        OfficeCodeCatalog.Usenet,
                    BusinessDatabaseName = "USENET",
                    SessionId = session.SessionId,
                    UserId = session.User!.UserId,
                    Status = "Failed",
                    ErrorMessage = "test pending item mutation",
                    PreparedAtUtc = now
                });
            db.DeferredRecycleBinPurgeRecords.AddRange(
                CreateDeferredPurgeRecord(
                    itemReceiptId,
                    "item",
                    itemId,
                    revision: 4,
                    businessDatabaseName: "USENET",
                    now),
                CreateDeferredPurgeRecord(
                    unknownReceiptId,
                    "future-server-kind",
                    Guid.NewGuid(),
                    revision: 4,
                    businessDatabaseName: "USENET",
                    now),
                CreateDeferredPurgeRecord(
                    foreignReceiptId,
                    "future-server-kind",
                    Guid.NewGuid(),
                    revision: 4,
                    businessDatabaseName: "ITWORLD",
                    now));
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 1
                });
            using var sync =
                CreateSyncService(db, session, handler);
            var syncTask = sync.TrySyncAsync();
            await handler.PullReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(15));
            handler.ReleasePull();
            Assert.True(
                await syncTask.WaitAsync(
                    TimeSpan.FromSeconds(15)));

            await using var verificationDb =
                new LocalDbContext();
            var ownerRecords = await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .Where(current =>
                    current.Id == itemReceiptId ||
                    current.Id == unknownReceiptId)
                .ToListAsync();
            var unsupportedRecord =
                Assert.Single(ownerRecords);
            Assert.Equal(
                unknownReceiptId,
                unsupportedRecord.Id);
            Assert.True(
                unsupportedRecord.AttemptCount >= 1);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    unsupportedRecord.LastErrorMessage));
            Assert.False(await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .AnyAsync(current =>
                    current.Id == itemReceiptId));
            var foreignRecord = await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .SingleAsync(current =>
                    current.Id == foreignReceiptId);
            Assert.Equal(0, foreignRecord.AttemptCount);
            Assert.True(await verificationDb.Items
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == itemId));
            Assert.Equal(
                "Failed",
                await verificationDb.SyncOutboxEntries
                    .AsNoTracking()
                    .Where(current =>
                        current.Id == pendingOutboxId)
                    .Select(current => current.Status)
                    .SingleAsync());
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RetryDeferredPurgeAsync_SameRevisionDirtyItemWithPendingExactScopeOutbox_RetainsReceiptItemAndOutbox()
    {
        PrepareAppRoot(
            "georaeplan-deferred-purge-same-revision-pending");

        try
        {
            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var outboxId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);

            await using (var db = new LocalDbContext())
            {
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
                db.Items.Add(new LocalItem
                {
                    Id = itemId,
                    TenantCode =
                        TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal =
                        "same revision pending purge item",
                    NameMatchKey =
                        "SAMEREVISIONPENDINGPURGEITEM",
                    Unit = "EA",
                    Revision = 5,
                    IsDirty = true,
                    IsDeleted = false,
                    CreatedAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now
                });
                db.SyncOutboxEntries.Add(
                    new LocalSyncOutboxEntry
                    {
                        Id = outboxId,
                        MutationId =
                            $"pending-item:{itemId:N}:5",
                        DeviceId =
                            "deferred-purge-test-device",
                        EntityName = nameof(LocalItem),
                        EntityId = itemId,
                        ExpectedRevision = 5,
                        TenantCode =
                            TenantScopeCatalog.UsenetGroup,
                        OfficeCode =
                            OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode =
                            OfficeCodeCatalog.Usenet,
                        BusinessDatabaseName = "USENET",
                        SessionId = session.SessionId,
                        UserId = session.User!.UserId,
                        Status = "Failed",
                        ErrorMessage =
                            "test pending exact-scope item mutation",
                        PreparedAtUtc = now
                    });
                db.DeferredRecycleBinPurgeRecords.Add(
                    CreateDeferredPurgeRecord(
                        receiptId,
                        "item",
                        itemId,
                        revision: 5,
                        businessDatabaseName: "USENET",
                        now));
                db.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await db.SaveChangesAsync();
            }

            var handler = new EmptyPushThenPullHandler();
            await using (var retryDb = new LocalDbContext())
            using (var sync = CreateSyncService(
                       retryDb,
                       session,
                       handler))
            {
                Assert.True(
                    await sync.TrySyncAsync()
                        .WaitAsync(TimeSpan.FromSeconds(15)));
            }
            Assert.Equal(1, handler.PushCount);

            await using var verificationDb =
                new LocalDbContext();
            var receipt = await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .SingleAsync(current =>
                    current.Id == receiptId);
            Assert.True(receipt.AttemptCount >= 1);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    receipt.LastErrorMessage));

            var item = await verificationDb.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current =>
                    current.Id == itemId);
            Assert.Equal(5, item.Revision);
            Assert.True(item.IsDirty);
            Assert.False(item.IsDeleted);

            var outbox = await verificationDb
                .SyncOutboxEntries
                .AsNoTracking()
                .SingleAsync(current =>
                    current.Id == outboxId);
            Assert.Equal("Failed", outbox.Status);
            Assert.Equal(nameof(LocalItem), outbox.EntityName);
            Assert.Equal(itemId, outbox.EntityId);
            Assert.Equal(5, outbox.ExpectedRevision);
            Assert.Equal("USENET", outbox.BusinessDatabaseName);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryDeferredPurgeAsync_StaleReceiptAgainstNewerItem_RetiresReceiptAndCannotPurgeLaterDeletedIncarnation(
        bool initiallyDeleted)
    {
        PrepareAppRoot(
            $"georaeplan-deferred-purge-superseded-{initiallyDeleted}");

        try
        {
            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);

            await using (var db = new LocalDbContext())
            {
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
                db.Items.Add(new LocalItem
                {
                    Id = itemId,
                    TenantCode =
                        TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal =
                        "newer incarnation purge item",
                    NameMatchKey =
                        "NEWERINCARNATIONPURGEITEM",
                    Unit = "EA",
                    Revision = 5,
                    IsDirty = false,
                    IsDeleted = initiallyDeleted,
                    CreatedAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now
                });
                db.DeferredRecycleBinPurgeRecords.Add(
                    CreateDeferredPurgeRecord(
                        receiptId,
                        "item",
                        itemId,
                        revision: 4,
                        businessDatabaseName: "USENET",
                        now));
                db.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await db.SaveChangesAsync();
            }

            var handler = new EmptyPushThenPullHandler();
            await using (var firstRetryDb =
                         new LocalDbContext())
            using (var firstSync = CreateSyncService(
                       firstRetryDb,
                       session,
                       handler))
            {
                Assert.True(
                    await firstSync.TrySyncAsync()
                        .WaitAsync(TimeSpan.FromSeconds(15)));
            }

            await using (var newerIncarnationDb =
                         new LocalDbContext())
            {
                Assert.False(await newerIncarnationDb
                    .DeferredRecycleBinPurgeRecords
                    .AsNoTracking()
                    .AnyAsync(current =>
                        current.Id == receiptId));
                var newerItem = await newerIncarnationDb.Items
                    .IgnoreQueryFilters()
                    .SingleAsync(current =>
                        current.Id == itemId);
                Assert.Equal(5, newerItem.Revision);
                Assert.Equal(
                    initiallyDeleted,
                    newerItem.IsDeleted);

                newerItem.Revision = 6;
                newerItem.IsDeleted = true;
                newerItem.IsDirty = false;
                newerItem.UpdatedAtUtc =
                    now.AddMinutes(1);
                await newerIncarnationDb.SaveChangesAsync();
            }

            await using (var secondRetryDb =
                         new LocalDbContext())
            using (var secondSync = CreateSyncService(
                       secondRetryDb,
                       session,
                       handler))
            {
                Assert.True(
                    await secondSync.TrySyncAsync()
                        .WaitAsync(TimeSpan.FromSeconds(15)));
            }

            await using var verificationDb =
                new LocalDbContext();
            Assert.False(await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .AnyAsync(current =>
                    current.Id == receiptId));
            var preservedLaterIncarnation =
                await verificationDb.Items
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(current =>
                        current.Id == itemId);
            Assert.Equal(
                6,
                preservedLaterIncarnation.Revision);
            Assert.True(
                preservedLaterIncarnation.IsDeleted);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RetryDeferredPurgeAsync_PostCommitFaultResolvesCommittedResultRemovesReceiptAndPublishesEventsExactlyOnce()
    {
        PrepareAppRoot(
            "georaeplan-deferred-purge-post-commit-ambiguity");

        try
        {
            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);

            await using (var setupDb = new LocalDbContext())
            {
                await setupDb.Database.EnsureDeletedAsync();
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Customers.Add(
                    CreateDeferredPurgeCustomer(
                        customerId,
                        now));
                setupDb.Invoices.Add(
                    CreateDeferredPurgeInvoice(
                        invoiceId,
                        customerId,
                        now,
                        isDeleted: true));
                setupDb.DeferredRecycleBinPurgeRecords.Add(
                    CreateDeferredPurgeRecord(
                        receiptId,
                        "invoice",
                        invoiceId,
                        revision: 5,
                        businessDatabaseName: "USENET",
                        now));
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await setupDb.SaveChangesAsync();
            }

            var notifier =
                new DesktopDataChangeNotifier();
            var invoiceHistoryEventCount = 0;
            var inventoryEventCount = 0;
            notifier.ItemInvoiceHistoryChanged +=
                (_, _) => invoiceHistoryEventCount++;
            notifier.InventoryStateChanged +=
                (_, _) => inventoryEventCount++;
            var postCommitHookCount = 0;
            var handler = new EmptyPushThenPullHandler();
            await using (var retryDb =
                         new LocalDbContext())
            using (var sync = CreateSyncService(
                       retryDb,
                       session,
                       handler,
                       notifier))
            {
                sync.AfterAttachmentCommitAsyncForTesting =
                    _ => Interlocked.Increment(
                             ref postCommitHookCount) == 1
                        ? Task.FromException(
                            new InvalidOperationException(
                                "simulated failure after deferred purge DB commit"))
                        : Task.CompletedTask;

                Assert.True(
                    await sync.TrySyncAsync()
                        .WaitAsync(
                            TimeSpan.FromSeconds(15)));
            }

            Assert.True(postCommitHookCount >= 2);
            Assert.Equal(
                1,
                invoiceHistoryEventCount);
            Assert.Equal(1, inventoryEventCount);
            await using var verificationDb =
                new LocalDbContext();
            Assert.False(await verificationDb.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == invoiceId));
            Assert.False(await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AnyAsync(current =>
                    current.Id == receiptId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RetryDeferredPurgeAsync_CustomerWithDirtyContractAndPendingOutbox_DefersWholePurgePlan()
    {
        PrepareAppRoot(
            "georaeplan-deferred-customer-dependent-contract");

        try
        {
            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var outboxId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);

            await using (var setupDb = new LocalDbContext())
            {
                await setupDb.Database.EnsureDeletedAsync();
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Customers.Add(
                    CreateDeferredPurgeCustomer(
                        customerId,
                        now,
                        isDeleted: true));
                setupDb.CustomerContracts.Add(
                    new LocalCustomerContract
                    {
                        Id = contractId,
                        CustomerId = customerId,
                        ContractType = "offline edited contract",
                        FileName = "dirty-contract.pdf",
                        Revision = 5,
                        IsDirty = true,
                        IsDeleted = false,
                        CreatedAtUtc =
                            now.AddHours(-1),
                        UpdatedAtUtc = now
                    });
                setupDb.SyncOutboxEntries.Add(
                    CreateDeferredPurgeOutbox(
                        outboxId,
                        nameof(LocalCustomerContract),
                        contractId,
                        expectedRevision: 5,
                        businessDatabaseName: "USENET",
                        session,
                        now));
                setupDb.DeferredRecycleBinPurgeRecords.Add(
                    CreateDeferredPurgeRecord(
                        receiptId,
                        "customer",
                        customerId,
                        revision: 5,
                        businessDatabaseName: "USENET",
                        now));
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await setupDb.SaveChangesAsync();
            }

            await using (var retryDb =
                         new LocalDbContext())
            using (var sync = CreateSyncService(
                       retryDb,
                       session,
                       new EmptyPushThenPullHandler()))
            {
                await InvokeRetryDeferredPurgeRecordsAsync(
                        sync)
                    .WaitAsync(
                        TimeSpan.FromSeconds(15));
            }

            await using var verificationDb =
                new LocalDbContext();
            Assert.True(await verificationDb.Customers
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == customerId));
            var contract = await verificationDb
                .CustomerContracts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current =>
                    current.Id == contractId);
            Assert.True(contract.IsDirty);
            Assert.False(contract.IsDeleted);
            Assert.True(await verificationDb
                .SyncOutboxEntries
                .AnyAsync(current =>
                    current.Id == outboxId &&
                    current.Status !=
                        "Acknowledged"));
            var receipt = await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AsNoTracking()
                .SingleAsync(current =>
                    current.Id == receiptId);
            Assert.True(receipt.AttemptCount >= 1);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    receipt.LastErrorMessage));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RetryDeferredPurgeAsync_CustomerWithCleanNewerContract_DefersWholePurgePlan()
    {
        PrepareAppRoot(
            "georaeplan-deferred-customer-newer-contract");

        try
        {
            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var contractId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);

            await using (var setupDb = new LocalDbContext())
            {
                await setupDb.Database.EnsureDeletedAsync();
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Customers.Add(
                    CreateDeferredPurgeCustomer(
                        customerId,
                        now,
                        isDeleted: true));
                setupDb.CustomerContracts.Add(
                    new LocalCustomerContract
                    {
                        Id = contractId,
                        CustomerId = customerId,
                        ContractType =
                            "server-clean newer contract",
                        FileName =
                            "newer-contract.pdf",
                        Revision = 6,
                        IsDirty = false,
                        IsDeleted = false,
                        CreatedAtUtc =
                            now.AddHours(-1),
                        UpdatedAtUtc =
                            now.AddMinutes(1)
                    });
                setupDb.DeferredRecycleBinPurgeRecords.Add(
                    CreateDeferredPurgeRecord(
                        receiptId,
                        "customer",
                        customerId,
                        revision: 5,
                        businessDatabaseName: "USENET",
                        now));
                await setupDb.SaveChangesAsync();
            }

            await using (var retryDb =
                         new LocalDbContext())
            using (var sync = CreateSyncService(
                       retryDb,
                       session,
                       new EmptyPushThenPullHandler()))
            {
                await InvokeRetryDeferredPurgeRecordsAsync(
                        sync)
                    .WaitAsync(
                        TimeSpan.FromSeconds(15));
            }

            await using var verificationDb =
                new LocalDbContext();
            Assert.True(await verificationDb.Customers
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == customerId));
            var contract = await verificationDb
                .CustomerContracts
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current =>
                    current.Id == contractId);
            Assert.Equal(6, contract.Revision);
            Assert.False(contract.IsDirty);
            Assert.True(await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AnyAsync(current =>
                    current.Id == receiptId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RetryDeferredPurgeAsync_ItemWithDirtyLinkedRentalAsset_DefersWholePurgePlan()
    {
        PrepareAppRoot(
            "georaeplan-deferred-item-dependent-rental-asset");

        try
        {
            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var outboxId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);

            await using (var setupDb = new LocalDbContext())
            {
                await setupDb.Database.EnsureDeletedAsync();
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Items.Add(new LocalItem
                {
                    Id = itemId,
                    TenantCode =
                        TenantScopeCatalog.UsenetGroup,
                    OfficeCode =
                        OfficeCodeCatalog.Usenet,
                    NameOriginal =
                        "deleted catalog item",
                    NameMatchKey =
                        "DELETEDCATALOGITEM",
                    Unit = "EA",
                    Revision = 5,
                    IsDirty = false,
                    IsDeleted = true,
                    CreatedAtUtc =
                        now.AddHours(-1),
                    UpdatedAtUtc = now
                });
                setupDb.RentalAssets.Add(
                    new LocalRentalAsset
                    {
                        Id = assetId,
                        TenantCode =
                            TenantScopeCatalog
                                .UsenetGroup,
                        OfficeCode =
                            OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode =
                            OfficeCodeCatalog.Usenet,
                        ManagementCompanyCode =
                            OfficeCodeCatalog.Usenet,
                        ManagementId =
                            $"M-{assetId:N}",
                        ManagementNumber =
                            $"MN-{assetId:N}",
                        AssetKey =
                            $"AK-{assetId:N}",
                        ItemId = itemId,
                        ItemName =
                            "offline edited rental asset",
                        Notes =
                            "must survive item receipt",
                        Revision = 5,
                        IsDirty = true,
                        IsDeleted = false,
                        CreatedAtUtc =
                            now.AddHours(-1),
                        UpdatedAtUtc = now
                    });
                setupDb.SyncOutboxEntries.Add(
                    CreateDeferredPurgeOutbox(
                        outboxId,
                        nameof(LocalRentalAsset),
                        assetId,
                        expectedRevision: 5,
                        businessDatabaseName: "USENET",
                        session,
                        now));
                setupDb.DeferredRecycleBinPurgeRecords.Add(
                    CreateDeferredPurgeRecord(
                        receiptId,
                        "item",
                        itemId,
                        revision: 5,
                        businessDatabaseName: "USENET",
                        now));
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await setupDb.SaveChangesAsync();
            }

            await using (var retryDb =
                         new LocalDbContext())
            using (var sync = CreateSyncService(
                       retryDb,
                       session,
                       new EmptyPushThenPullHandler()))
            {
                await InvokeRetryDeferredPurgeRecordsAsync(
                        sync)
                    .WaitAsync(
                        TimeSpan.FromSeconds(15));
            }

            await using var verificationDb =
                new LocalDbContext();
            Assert.True(await verificationDb.Items
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == itemId));
            var asset = await verificationDb
                .RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current =>
                    current.Id == assetId);
            Assert.Equal(itemId, asset.ItemId);
            Assert.True(asset.IsDirty);
            Assert.Equal(
                "must survive item receipt",
                asset.Notes);
            Assert.True(await verificationDb
                .SyncOutboxEntries
                .AnyAsync(current =>
                    current.Id == outboxId &&
                    current.Status !=
                        "Acknowledged"));
            Assert.True(await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AnyAsync(current =>
                    current.Id == receiptId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyServerPurgeItem_ForeignInventoryTransferParent_DefersWholePurgePlan()
    {
        PrepareAppRoot(
            "georaeplan-item-purge-foreign-transfer-parent");

        try
        {
            var itemId = Guid.NewGuid();
            var transferId = Guid.NewGuid();
            var transferLineId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode =
                    TenantScopeCatalog.UsenetGroup,
                OfficeCode =
                    OfficeCodeCatalog.Usenet,
                NameOriginal =
                    "USENET deleted catalog item",
                NameMatchKey =
                    "USENETDELETEDCATALOGITEM",
                Unit = "EA",
                Revision = 5,
                IsDirty = false,
                IsDeleted = true,
                CreatedAtUtc =
                    now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.InventoryTransfers.Add(
                new LocalInventoryTransfer
                {
                    Id = transferId,
                    TransferNumber =
                        "ITWORLD-FOREIGN-TRANSFER",
                    TransferDate =
                        DateOnly.FromDateTime(now),
                    FromWarehouseCode =
                        OfficeCodeCatalog
                            .ItworldMainWarehouse,
                    ToWarehouseCode =
                        OfficeCodeCatalog
                            .ItworldMainWarehouse,
                    TransferStatus = "이동완료",
                    Revision = 5,
                    IsDirty = false,
                    IsDeleted = false,
                    CreatedAtUtc =
                        now.AddHours(-1),
                    UpdatedAtUtc = now,
                    Lines =
                    [
                        new LocalInventoryTransferLine
                        {
                            Id = transferLineId,
                            TransferId =
                                transferId,
                            ItemId = itemId,
                            ItemNameOriginal =
                                "foreign transfer item",
                            Unit = "EA",
                            Quantity = 1m
                        }
                    ]
                });
            await db.SaveChangesAsync();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());
            var result =
                await service
                    .ApplyServerPurgeRecycleBinEntryAsync(
                        RecycleBinEntityKind.Item,
                        itemId,
                        purgeRevision: 5,
                        businessDatabaseName:
                            "USENET");

            Assert.False(result.Success);
            db.ChangeTracker.Clear();
            Assert.True(await db.Items
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == itemId));
            var transferLine = await db
                .InventoryTransferLines
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current =>
                    current.Id == transferLineId);
            Assert.Equal(
                itemId,
                transferLine.ItemId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyServerPurgeRentalManagementCompany_ForeignCode_DefersPurge()
    {
        PrepareAppRoot(
            "georaeplan-rental-company-purge-foreign-code");

        try
        {
            var companyId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.RentalManagementCompanies.Add(
                new LocalRentalManagementCompany
                {
                    Id = companyId,
                    Code =
                        OfficeCodeCatalog.Itworld,
                    Name =
                        "ITWORLD management company",
                    IsActive = false,
                    Revision = 5,
                    IsDirty = false,
                    IsDeleted = true,
                    CreatedAtUtc =
                        now.AddHours(-1),
                    UpdatedAtUtc = now
                });
            await db.SaveChangesAsync();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());
            var result =
                await service
                    .ApplyServerPurgeRecycleBinEntryAsync(
                        RecycleBinEntityKind
                            .RentalManagementCompany,
                        companyId,
                        purgeRevision: 5,
                        businessDatabaseName:
                            "USENET");

            Assert.False(result.Success);
            db.ChangeTracker.Clear();
            Assert.True(await db
                .RentalManagementCompanies
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == companyId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyServerPurgePayment_UsesLinkedInvoiceBusinessDatabaseBeforeSameIdTransaction()
    {
        PrepareAppRoot(
            "georaeplan-payment-purge-authoritative-invoice-scope");

        try
        {
            var usenetCustomerId = Guid.NewGuid();
            var itworldCustomerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var sharedId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.Customers.Add(
                CreateDeferredPurgeCustomer(
                    usenetCustomerId,
                    now));
            db.Customers.Add(new LocalCustomer
            {
                Id = itworldCustomerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode =
                    OfficeCodeCatalog.Itworld,
                NameOriginal =
                    "ITWORLD same-id transaction customer",
                NameMatchKey =
                    "ITWORLDSAMEIDTRANSACTIONCUSTOMER",
                Revision = 5,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Invoices.Add(
                CreateDeferredPurgeInvoice(
                    invoiceId,
                    usenetCustomerId,
                    now));
            db.Payments.Add(new LocalPayment
            {
                Id = sharedId,
                InvoiceId = invoiceId,
                PaymentDate =
                    new DateOnly(2026, 7, 31),
                Amount = 40_000m,
                Revision = 5,
                IsDirty = false,
                IsDeleted = true,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Transactions.Add(
                new LocalTransaction
                {
                    Id = sharedId,
                    CustomerId =
                        itworldCustomerId,
                    TenantCode =
                        TenantScopeCatalog.Itworld,
                    OfficeCode =
                        OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode =
                        OfficeCodeCatalog.Itworld,
                    TransactionKind =
                        PaymentFlowConstants
                            .TransactionKindReceipt,
                    ReceiptTotal = 40_000m,
                    Revision = 5,
                    IsDirty = false,
                    IsDeleted = true,
                    CreatedAtUtc =
                        now.AddHours(-1),
                    UpdatedAtUtc = now
                });
            await db.SaveChangesAsync();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());
            var result =
                await service
                    .ApplyServerPurgeRecycleBinEntryAsync(
                        RecycleBinEntityKind.Payment,
                        sharedId,
                        purgeRevision: 5);

            Assert.False(result.Success);
            db.ChangeTracker.Clear();
            Assert.True(await db.Payments
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == sharedId));
            Assert.True(await db.Transactions
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == sharedId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ApplyServerPurgeTransaction_RejectsSameIdPaymentFromForeignInvoiceBusinessDatabase()
    {
        PrepareAppRoot(
            "georaeplan-transaction-purge-foreign-payment-scope");

        try
        {
            var usenetCustomerId = Guid.NewGuid();
            var itworldCustomerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var sharedId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.Customers.Add(
                CreateDeferredPurgeCustomer(
                    usenetCustomerId,
                    now));
            db.Customers.Add(new LocalCustomer
            {
                Id = itworldCustomerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode =
                    OfficeCodeCatalog.Itworld,
                NameOriginal =
                    "ITWORLD transaction target customer",
                NameMatchKey =
                    "ITWORLDTRANSACTIONTARGETCUSTOMER",
                Revision = 5,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Invoices.Add(
                CreateDeferredPurgeInvoice(
                    invoiceId,
                    usenetCustomerId,
                    now));
            db.Payments.Add(new LocalPayment
            {
                Id = sharedId,
                InvoiceId = invoiceId,
                PaymentDate =
                    new DateOnly(2026, 7, 31),
                Amount = 50_000m,
                Revision = 5,
                IsDirty = false,
                IsDeleted = true,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Transactions.Add(
                new LocalTransaction
                {
                    Id = sharedId,
                    CustomerId =
                        itworldCustomerId,
                    TenantCode =
                        TenantScopeCatalog.Itworld,
                    OfficeCode =
                        OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode =
                        OfficeCodeCatalog.Itworld,
                    TransactionKind =
                        PaymentFlowConstants
                            .TransactionKindReceipt,
                    ReceiptTotal = 50_000m,
                    Revision = 5,
                    IsDirty = false,
                    IsDeleted = true,
                    CreatedAtUtc =
                        now.AddHours(-1),
                    UpdatedAtUtc = now
                });
            await db.SaveChangesAsync();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());
            var result =
                await service
                    .ApplyServerPurgeRecycleBinEntryAsync(
                        RecycleBinEntityKind.Transaction,
                        sharedId,
                        purgeRevision: 5);

            Assert.False(result.Success);
            db.ChangeTracker.Clear();
            Assert.True(await db.Payments
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == sharedId));
            Assert.True(await db.Transactions
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == sharedId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RetryDeferredPurgeAsync_RentalProfileWithDirtyLinkedAsset_DefersWholePurgePlan()
    {
        PrepareAppRoot(
            "georaeplan-deferred-rental-profile-dependent-asset");

        try
        {
            var session = CreateAdminSession();
            var profileId = Guid.NewGuid();
            var assetId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);

            await using (var setupDb = new LocalDbContext())
            {
                await setupDb.Database.EnsureDeletedAsync();
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.RentalBillingProfiles.Add(
                    new LocalRentalBillingProfile
                    {
                        Id = profileId,
                        TenantCode =
                            TenantScopeCatalog
                                .UsenetGroup,
                        OfficeCode =
                            OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode =
                            OfficeCodeCatalog.Usenet,
                        ManagementCompanyCode =
                            OfficeCodeCatalog.Usenet,
                        ProfileKey =
                            $"PROFILE-{profileId:N}",
                        CustomerName =
                            "deleted rental profile",
                        Revision = 5,
                        IsDirty = false,
                        IsDeleted = true,
                        CreatedAtUtc =
                            now.AddHours(-1),
                        UpdatedAtUtc = now
                    });
                setupDb.RentalAssets.Add(
                    new LocalRentalAsset
                    {
                        Id = assetId,
                        TenantCode =
                            TenantScopeCatalog
                                .UsenetGroup,
                        OfficeCode =
                            OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode =
                            OfficeCodeCatalog.Usenet,
                        ManagementCompanyCode =
                            OfficeCodeCatalog.Usenet,
                        ManagementId =
                            $"M-{assetId:N}",
                        ManagementNumber =
                            $"MN-{assetId:N}",
                        AssetKey =
                            $"AK-{assetId:N}",
                        BillingProfileId = profileId,
                        Notes =
                            "offline rental asset edit",
                        Revision = 5,
                        IsDirty = true,
                        IsDeleted = false,
                        CreatedAtUtc =
                            now.AddHours(-1),
                        UpdatedAtUtc = now
                    });
                setupDb.DeferredRecycleBinPurgeRecords.Add(
                    CreateDeferredPurgeRecord(
                        receiptId,
                        "rental-billing-profile",
                        profileId,
                        revision: 5,
                        businessDatabaseName: "USENET",
                        now));
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await setupDb.SaveChangesAsync();
            }

            await using (var retryDb =
                         new LocalDbContext())
            using (var sync = CreateSyncService(
                       retryDb,
                       session,
                       new EmptyPushThenPullHandler()))
            {
                Assert.True(
                    await sync.TrySyncAsync()
                        .WaitAsync(
                            TimeSpan.FromSeconds(15)));
            }

            await using var verificationDb =
                new LocalDbContext();
            Assert.True(await verificationDb
                .RentalBillingProfiles
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == profileId));
            var asset = await verificationDb
                .RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current =>
                    current.Id == assetId);
            Assert.Equal(
                profileId,
                asset.BillingProfileId);
            Assert.True(asset.IsDirty);
            Assert.True(await verificationDb
                .DeferredRecycleBinPurgeRecords
                .AnyAsync(current =>
                    current.Id == receiptId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RetryDeferredPurgeAsync_TransactionReceiptCannotDeleteDirtySameIdPaymentDeferredEarlier()
    {
        PrepareAppRoot(
            "georaeplan-deferred-transaction-dependent-payment");

        try
        {
            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var sharedId = Guid.NewGuid();
            var paymentReceiptId = Guid.NewGuid();
            var transactionReceiptId = Guid.NewGuid();
            var outboxId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);

            await using (var setupDb = new LocalDbContext())
            {
                await setupDb.Database.EnsureDeletedAsync();
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Customers.Add(
                    CreateDeferredPurgeCustomer(
                        customerId,
                        now));
                setupDb.Invoices.Add(
                    CreateDeferredPurgeInvoice(
                        invoiceId,
                        customerId,
                        now));
                setupDb.Payments.Add(new LocalPayment
                {
                    Id = sharedId,
                    InvoiceId = invoiceId,
                    PaymentDate =
                        new DateOnly(2026, 7, 31),
                    Amount = 10_000m,
                    Note =
                        "dirty payment must survive",
                    Revision = 5,
                    IsDirty = true,
                    IsDeleted = true,
                    CreatedAtUtc =
                        now.AddHours(-1),
                    UpdatedAtUtc = now
                });
                setupDb.Transactions.Add(
                    new LocalTransaction
                    {
                        Id = sharedId,
                        CustomerId = customerId,
                        TenantCode =
                            TenantScopeCatalog
                                .UsenetGroup,
                        OfficeCode =
                            OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode =
                            OfficeCodeCatalog.Usenet,
                        TransactionKind =
                            PaymentFlowConstants
                                .TransactionKindReceipt,
                        ReceiptTotal = 10_000m,
                        Revision = 5,
                        IsDirty = false,
                        IsDeleted = true,
                        CreatedAtUtc =
                            now.AddHours(-1),
                        UpdatedAtUtc = now
                    });
                setupDb.SyncOutboxEntries.Add(
                    CreateDeferredPurgeOutbox(
                        outboxId,
                        nameof(LocalPayment),
                        sharedId,
                        expectedRevision: 5,
                        businessDatabaseName: "USENET",
                        session,
                        now));
                setupDb.DeferredRecycleBinPurgeRecords
                    .AddRange(
                        CreateDeferredPurgeRecord(
                            paymentReceiptId,
                            "payment",
                            sharedId,
                            revision: 5,
                            businessDatabaseName:
                                "USENET",
                            now),
                        CreateDeferredPurgeRecord(
                            transactionReceiptId,
                            "transaction",
                            sharedId,
                            revision: 5,
                            businessDatabaseName:
                                "USENET",
                            now));
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await setupDb.SaveChangesAsync();
            }

            await using (var retryDb =
                         new LocalDbContext())
            using (var sync = CreateSyncService(
                       retryDb,
                       session,
                       new EmptyPushThenPullHandler()))
            {
                Assert.True(
                    await sync.TrySyncAsync()
                        .WaitAsync(
                            TimeSpan.FromSeconds(15)));
            }

            await using var verificationDb =
                new LocalDbContext();
            Assert.True(await verificationDb.Payments
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == sharedId &&
                    current.IsDirty));
            Assert.True(await verificationDb.Transactions
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == sharedId));
            Assert.True(await verificationDb
                .SyncOutboxEntries
                .AnyAsync(current =>
                    current.Id == outboxId &&
                    current.Status !=
                        "Acknowledged"));
            var receiptIds = await verificationDb
                .DeferredRecycleBinPurgeRecords
                .Where(current =>
                    current.Id == paymentReceiptId ||
                    current.Id ==
                        transactionReceiptId)
                .Select(current => current.Id)
                .ToListAsync();
            Assert.Equal(2, receiptIds.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RetryDeferredPurgeAsync_PaymentReceiptRemovesCleanActiveSameIdTransaction()
    {
        PrepareAppRoot(
            "georaeplan-deferred-payment-clean-active-transaction");

        try
        {
            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var sharedId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);

            await using (var setupDb = new LocalDbContext())
            {
                await setupDb.Database.EnsureDeletedAsync();
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Customers.Add(
                    CreateDeferredPurgeCustomer(
                        customerId,
                        now));
                setupDb.Invoices.Add(
                    CreateDeferredPurgeInvoice(
                        invoiceId,
                        customerId,
                        now));
                setupDb.Payments.Add(new LocalPayment
                {
                    Id = sharedId,
                    InvoiceId = invoiceId,
                    PaymentDate =
                        new DateOnly(2026, 7, 31),
                    Amount = 30_000m,
                    Revision = 5,
                    IsDirty = false,
                    IsDeleted = true,
                    CreatedAtUtc =
                        now.AddHours(-1),
                    UpdatedAtUtc = now
                });
                setupDb.Transactions.Add(
                    new LocalTransaction
                    {
                        Id = sharedId,
                        CustomerId = customerId,
                        TenantCode =
                            TenantScopeCatalog
                                .UsenetGroup,
                        OfficeCode =
                            OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode =
                            OfficeCodeCatalog.Usenet,
                        TransactionKind =
                            PaymentFlowConstants
                                .TransactionKindReceipt,
                        ReceiptTotal = 30_000m,
                        Revision = 5,
                        IsDirty = false,
                        IsDeleted = false,
                        CreatedAtUtc =
                            now.AddHours(-1),
                        UpdatedAtUtc = now
                    });
                setupDb.DeferredRecycleBinPurgeRecords.Add(
                    CreateDeferredPurgeRecord(
                        receiptId,
                        "payment",
                        sharedId,
                        revision: 5,
                        businessDatabaseName:
                            "USENET",
                        now));
                await setupDb.SaveChangesAsync();
            }

            await using (var retryDb =
                         new LocalDbContext())
            using (var sync = CreateSyncService(
                       retryDb,
                       session,
                       new EmptyPushThenPullHandler()))
            {
                await InvokeRetryDeferredPurgeRecordsAsync(
                        sync)
                    .WaitAsync(
                        TimeSpan.FromSeconds(15));
            }

            await using var verificationDb =
                new LocalDbContext();
            var actual = (
                PaymentExists:
                    await verificationDb.Payments
                        .IgnoreQueryFilters()
                        .AnyAsync(current =>
                            current.Id == sharedId),
                TransactionExists:
                    await verificationDb.Transactions
                        .IgnoreQueryFilters()
                        .AnyAsync(current =>
                            current.Id == sharedId),
                ReceiptExists:
                    await verificationDb
                        .DeferredRecycleBinPurgeRecords
                        .AnyAsync(current =>
                            current.Id == receiptId));
            Assert.Equal(
                (
                    PaymentExists: false,
                    TransactionExists: false,
                    ReceiptExists: false),
                actual);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task RetryDeferredPurgeAsync_PaymentReceiptCannotDeleteDirtySameIdTransactionAndAttachment()
    {
        PrepareAppRoot(
            "georaeplan-deferred-payment-dependent-transaction");

        try
        {
            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var sharedId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var paymentReceiptId = Guid.NewGuid();
            var transactionReceiptId = Guid.NewGuid();
            var outboxId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);

            await using (var setupDb = new LocalDbContext())
            {
                await setupDb.Database.EnsureDeletedAsync();
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Customers.Add(
                    CreateDeferredPurgeCustomer(
                        customerId,
                        now));
                setupDb.Invoices.Add(
                    CreateDeferredPurgeInvoice(
                        invoiceId,
                        customerId,
                        now));
                setupDb.Payments.Add(new LocalPayment
                {
                    Id = sharedId,
                    InvoiceId = invoiceId,
                    PaymentDate =
                        new DateOnly(2026, 7, 31),
                    Amount = 20_000m,
                    Revision = 5,
                    IsDirty = false,
                    IsDeleted = true,
                    CreatedAtUtc =
                        now.AddHours(-1),
                    UpdatedAtUtc = now
                });
                setupDb.Transactions.Add(
                    new LocalTransaction
                    {
                        Id = sharedId,
                        CustomerId = customerId,
                        TenantCode =
                            TenantScopeCatalog
                                .UsenetGroup,
                        OfficeCode =
                            OfficeCodeCatalog.Usenet,
                        ResponsibleOfficeCode =
                            OfficeCodeCatalog.Usenet,
                        TransactionKind =
                            PaymentFlowConstants
                                .TransactionKindReceipt,
                        ReceiptTotal = 20_000m,
                        Memo =
                            "dirty transaction must survive",
                        Revision = 5,
                        IsDirty = true,
                        IsDeleted = true,
                        CreatedAtUtc =
                            now.AddHours(-1),
                        UpdatedAtUtc = now
                    });
                setupDb.TransactionAttachments.Add(
                    new LocalTransactionAttachment
                    {
                        Id = attachmentId,
                        TransactionId = sharedId,
                        AttachmentType = "기타",
                        FileName =
                            "dirty-attachment.pdf",
                        StoredFileName =
                            "dirty-attachment.pdf",
                        StoredPath = string.Empty,
                        Revision = 5,
                        IsDirty = true,
                        IsDeleted = false,
                        CreatedAtUtc =
                            now.AddHours(-1),
                        UpdatedAtUtc = now
                    });
                setupDb.SyncOutboxEntries.Add(
                    CreateDeferredPurgeOutbox(
                        outboxId,
                        nameof(
                            LocalTransactionAttachment),
                        attachmentId,
                        expectedRevision: 5,
                        businessDatabaseName: "USENET",
                        session,
                        now));
                setupDb.DeferredRecycleBinPurgeRecords
                    .AddRange(
                        CreateDeferredPurgeRecord(
                            paymentReceiptId,
                            "payment",
                            sharedId,
                            revision: 5,
                            businessDatabaseName:
                                "USENET",
                            now),
                        CreateDeferredPurgeRecord(
                            transactionReceiptId,
                            "transaction",
                            sharedId,
                            revision: 5,
                            businessDatabaseName:
                                "USENET",
                            now));
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await setupDb.SaveChangesAsync();
            }

            await using (var retryDb =
                         new LocalDbContext())
            using (var sync = CreateSyncService(
                       retryDb,
                       session,
                       new EmptyPushThenPullHandler()))
            {
                Assert.True(
                    await sync.TrySyncAsync()
                        .WaitAsync(
                            TimeSpan.FromSeconds(15)));
            }

            await using var verificationDb =
                new LocalDbContext();
            Assert.True(await verificationDb.Payments
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == sharedId));
            Assert.True(await verificationDb.Transactions
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == sharedId &&
                    current.IsDirty));
            Assert.True(await verificationDb
                .TransactionAttachments
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == attachmentId &&
                    current.IsDirty));
            Assert.True(await verificationDb
                .SyncOutboxEntries
                .AnyAsync(current =>
                    current.Id == outboxId &&
                    current.Status !=
                        "Acknowledged"));
            var receiptIds = await verificationDb
                .DeferredRecycleBinPurgeRecords
                .Where(current =>
                    current.Id == paymentReceiptId ||
                    current.Id ==
                        transactionReceiptId)
                .Select(current => current.Id)
                .ToListAsync();
            Assert.Equal(2, receiptIds.Count);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Theory]
    [InlineData("ITWORLD", true)]
    [InlineData("USENET", false)]
    public async Task RetryDeferredPurgeAsync_ItemPendingOutboxUsesExactBusinessDatabase(
        string outboxBusinessDatabaseName,
        bool expectPurgeApplied)
    {
        PrepareAppRoot(
            $"georaeplan-deferred-item-outbox-db-{outboxBusinessDatabaseName}");

        try
        {
            var session = CreateAdminSession();
            var itemId = Guid.NewGuid();
            var receiptId = Guid.NewGuid();
            var outboxId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);

            await using (var setupDb = new LocalDbContext())
            {
                await setupDb.Database.EnsureDeletedAsync();
                await setupDb.Database.EnsureCreatedAsync();
                setupDb.Items.Add(new LocalItem
                {
                    Id = itemId,
                    TenantCode =
                        TenantScopeCatalog.UsenetGroup,
                    OfficeCode =
                        OfficeCodeCatalog.Usenet,
                    NameOriginal =
                        "cross database purge item",
                    NameMatchKey =
                        "CROSSDATABASEPURGEITEM",
                    Unit = "EA",
                    Revision = 5,
                    IsDirty = false,
                    IsDeleted = true,
                    CreatedAtUtc =
                        now.AddHours(-1),
                    UpdatedAtUtc = now
                });
                setupDb.SyncOutboxEntries.Add(
                    CreateDeferredPurgeOutbox(
                        outboxId,
                        nameof(LocalItem),
                        itemId,
                        expectedRevision: 5,
                        businessDatabaseName:
                            outboxBusinessDatabaseName,
                        session,
                        now));
                setupDb.DeferredRecycleBinPurgeRecords.Add(
                    CreateDeferredPurgeRecord(
                        receiptId,
                        "item",
                        itemId,
                        revision: 5,
                        businessDatabaseName: "USENET",
                        now));
                setupDb.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await setupDb.SaveChangesAsync();
            }

            await using (var retryDb =
                         new LocalDbContext())
            using (var sync = CreateSyncService(
                       retryDb,
                       session,
                       new EmptyPushThenPullHandler()))
            {
                Assert.True(
                    await sync.TrySyncAsync()
                        .WaitAsync(
                            TimeSpan.FromSeconds(15)));
            }

            await using var verificationDb =
                new LocalDbContext();
            Assert.Equal(
                expectPurgeApplied,
                !await verificationDb.Items
                    .IgnoreQueryFilters()
                    .AnyAsync(current =>
                        current.Id == itemId));
            Assert.Equal(
                expectPurgeApplied,
                !await verificationDb
                    .DeferredRecycleBinPurgeRecords
                    .AnyAsync(current =>
                        current.Id == receiptId));
            Assert.True(await verificationDb
                .SyncOutboxEntries
                .AnyAsync(current =>
                    current.Id == outboxId &&
                    current.Status !=
                        "Acknowledged"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TrySyncAsync_FailureAfterServerPurge_RollsBackRowCursorAndAttachmentFile()
    {
        PrepareAppRoot("georaeplan-incremental-purge-file-atomicity");

        var transactionId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachmentPath = Path.Combine(
            AppPaths.TransactionAttachmentsDir,
            transactionId.ToString("N"),
            $"{attachmentId:N}_purge-rollback.pdf");

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(attachmentPath)!);
            await File.WriteAllBytesAsync(
                attachmentPath,
                "%PDF-1.4\npurge rollback evidence"u8.ToArray());

            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var now = DateTime.UtcNow.AddMinutes(-5);
            db.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                ReceiptTotal = 10m,
                Revision = 1,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = attachmentId,
                TransactionId = transactionId,
                AttachmentType = "기타",
                FileName = "purge-rollback.pdf",
                StoredFileName = Path.GetFileName(attachmentPath),
                StoredPath = attachmentPath,
                MimeType = "application/pdf",
                FileSize = new FileInfo(attachmentPath).Length,
                Revision = 1,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 2,
                    PurgeRecords =
                    [
                        new RecycleBinPurgeRecordDto
                        {
                            Id = Guid.NewGuid(),
                            Kind = "transaction",
                            EntityId = transactionId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            PurgedAtUtc = now.AddMinutes(1),
                            Revision = 2,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now.AddMinutes(1)
                        }
                    ]
                });
            using var sync = CreateSyncService(db, session, handler);
            sync.AfterPulledPurgeRecordsAsyncForTesting =
                _ => throw new InvalidOperationException(
                    "simulated failure after purge rows were saved");

            var syncTask = sync.TrySyncAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.ReleasePull();

            Assert.False(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            await using var verificationDb = new LocalDbContext();
            Assert.NotNull(await verificationDb.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(current => current.Id == transactionId));
            Assert.NotNull(await verificationDb.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(current => current.Id == attachmentId));
            Assert.Equal(
                "1",
                await verificationDb.Settings
                    .AsNoTracking()
                    .Where(setting => setting.Key == "LastSyncRevision")
                    .Select(setting => setting.Value)
                    .SingleAsync());
            Assert.True(File.Exists(attachmentPath));
        }
        finally
        {
            if (File.Exists(attachmentPath))
                File.Delete(attachmentPath);

            var attachmentDirectory = Path.GetDirectoryName(attachmentPath);
            if (!string.IsNullOrWhiteSpace(attachmentDirectory) &&
                Directory.Exists(attachmentDirectory) &&
                !Directory.EnumerateFileSystemEntries(attachmentDirectory).Any())
            {
                Directory.Delete(attachmentDirectory);
            }

            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task TrySyncAsync_InvoicePurgeFailure_SuppressesInventoryAndHistoryEventsUntilCommit()
    {
        PrepareAppRoot("georaeplan-invoice-purge-event-rollback");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Invoice purge rollback customer",
                NameMatchKey = "INVOICEPURGEROLLBACKCUSTOMER",
                Revision = 1,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                VoucherType = VoucherType.Sales,
                InvoiceDate = new DateOnly(2026, 7, 21),
                VersionGroupId = invoiceId,
                IsLatestVersion = true,
                IsConfirmed = true,
                Revision = 1,
                IsDirty = false,
                CreatedAtUtc = now.AddHours(-1),
                UpdatedAtUtc = now
            });
            db.Settings.Add(new LocalSetting
            {
                Key = "LastSyncRevision",
                Value = "1"
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 2,
                    PurgeRecords =
                    [
                        new RecycleBinPurgeRecordDto
                        {
                            Id = Guid.NewGuid(),
                            Kind = " Invoice ",
                            EntityId = invoiceId,
                            TenantCode = TenantScopeCatalog.UsenetGroup,
                            OfficeCode = OfficeCodeCatalog.Usenet,
                            PurgedAtUtc = now.AddMinutes(1),
                            Revision = 2,
                            CreatedAtUtc = now,
                            UpdatedAtUtc = now.AddMinutes(1)
                        }
                    ]
                });
            var notifier = new DesktopDataChangeNotifier();
            var invoiceHistoryEventCount = 0;
            var inventoryEventCount = 0;
            notifier.ItemInvoiceHistoryChanged += (_, _) => invoiceHistoryEventCount++;
            notifier.InventoryStateChanged += (_, _) => inventoryEventCount++;
            using var sync = CreateSyncService(db, session, handler, notifier);
            sync.AfterPulledPurgeRecordsAsyncForTesting = _ =>
            {
                Assert.Equal(0, invoiceHistoryEventCount);
                Assert.Equal(0, inventoryEventCount);
                throw new InvalidOperationException(
                    "simulated failure after invoice purge rows were saved");
            };

            var syncTask = sync.TrySyncAsync();
            await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            handler.ReleasePull();

            Assert.False(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.Equal(0, invoiceHistoryEventCount);
            Assert.Equal(0, inventoryEventCount);
            await using var verificationDb = new LocalDbContext();
            Assert.NotNull(await verificationDb.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleOrDefaultAsync(invoice => invoice.Id == invoiceId));
            Assert.Equal(
                "1",
                await verificationDb.Settings
                    .AsNoTracking()
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
    public async Task TrySyncAsync_InvoicePurgeSuccess_PublishesInventoryAndHistoryEventsExactlyOnceAfterCommit()
    {
        PrepareAppRoot(
            "georaeplan-invoice-purge-event-success");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var now = DateTime.UtcNow.AddMinutes(-5);
            db.Customers.Add(
                new LocalCustomer
                {
                    Id = customerId,
                    TenantCode =
                        TenantScopeCatalog.UsenetGroup,
                    OfficeCode =
                        OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode =
                        OfficeCodeCatalog.Usenet,
                    NameOriginal =
                        "Invoice purge event customer",
                    NameMatchKey =
                        "INVOICEPURGEEVENTCUSTOMER",
                    Revision = 1,
                    IsDirty = false,
                    CreatedAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now
                });
            db.Invoices.Add(
                new LocalInvoice
                {
                    Id = invoiceId,
                    CustomerId = customerId,
                    TenantCode =
                        TenantScopeCatalog.UsenetGroup,
                    OfficeCode =
                        OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode =
                        OfficeCodeCatalog.Usenet,
                    VoucherType = VoucherType.Sales,
                    InvoiceDate =
                        new DateOnly(2026, 7, 21),
                    VersionGroupId = invoiceId,
                    IsLatestVersion = true,
                    IsConfirmed = true,
                    Revision = 1,
                    IsDirty = false,
                    CreatedAtUtc = now.AddHours(-1),
                    UpdatedAtUtc = now
                });
            db.Settings.Add(
                new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPullHandler(
                response: new SyncPullResponse
                {
                    CurrentServerRevision = 2,
                    PurgeRecords =
                    [
                        new RecycleBinPurgeRecordDto
                        {
                            Id = Guid.NewGuid(),
                            Kind = "invoice",
                            EntityId = invoiceId,
                            TenantCode =
                                TenantScopeCatalog
                                    .UsenetGroup,
                            OfficeCode =
                                OfficeCodeCatalog
                                    .Usenet,
                            PurgedAtUtc =
                                now.AddMinutes(1),
                            Revision = 2,
                            CreatedAtUtc = now,
                            UpdatedAtUtc =
                                now.AddMinutes(1)
                        }
                    ]
                });
            var notifier =
                new DesktopDataChangeNotifier();
            var invoiceHistoryEventCount = 0;
            var inventoryEventCount = 0;
            notifier.ItemInvoiceHistoryChanged +=
                (_, _) =>
                {
                    invoiceHistoryEventCount++;
                    throw new InvalidOperationException(
                        "simulated post-commit history subscriber failure");
                };
            notifier.InventoryStateChanged +=
                (_, _) => inventoryEventCount++;
            using var sync = CreateSyncService(
                db,
                session,
                handler,
                notifier);
            sync.AfterPulledPurgeRecordsAsyncForTesting =
                _ =>
                {
                    Assert.Equal(
                        0,
                        invoiceHistoryEventCount);
                    Assert.Equal(
                        0,
                        inventoryEventCount);
                    return Task.CompletedTask;
                };

            var syncTask = sync.TrySyncAsync();
            await handler.PullReceived.Task.WaitAsync(
                TimeSpan.FromSeconds(15));
            handler.ReleasePull();

            Assert.True(await syncTask.WaitAsync(
                TimeSpan.FromSeconds(15)));
            Assert.Equal(
                1,
                invoiceHistoryEventCount);
            Assert.Equal(
                1,
                inventoryEventCount);
            await using var verificationDb =
                new LocalDbContext();
            Assert.False(await verificationDb.Invoices
                .IgnoreQueryFilters()
                .AnyAsync(current =>
                    current.Id == invoiceId));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_DelayedTaxNumberAck_PreservesConcurrentManualTaxInvoiceNumberAndDirtyState()
    {
        PrepareAppRoot("georaeplan-delayed-manual-tax-number-ack-race");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var acceptedUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(1);
            var newerUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const long originalRevision = 41;
            const long acceptedRevision = 42;
            const string manualTaxInvoiceNumber = "MANUAL-001";
            const string serverAssignedTaxInvoiceNumber = "SERVER-TAX-ACK-0001";

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "수동 세금계산서 번호 경합 거래처",
                NameMatchKey = "수동 세금계산서 번호 경합 거래처",
                Revision = 1,
                IsDirty = false,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-2),
                UpdatedAtUtc = originalUpdatedAtUtc.AddHours(-1)
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "S-MANUAL-TAX-RACE-0001",
                LocalTempNumber = "LOCAL-MANUAL-TAX-RACE-0001",
                TaxInvoiceNumber = string.Empty,
                VoucherType = VoucherType.Sales,
                InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
                Revision = originalRevision,
                IsDirty = true,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPushAckThenEmptyPullHandler(
                invoiceId,
                entityName: "Invoice",
                acceptedRevision,
                acceptedUpdatedAtUtc,
                assignedTaxInvoiceNumber: serverAssignedTaxInvoiceNumber);
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            var pushedRequest = await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var pushedInvoice = Assert.Single(pushedRequest.Invoices, invoice => invoice.Id == invoiceId);
            Assert.Equal(string.Empty, pushedInvoice.TaxInvoiceNumber);
            Assert.Equal(originalRevision, pushedInvoice.ExpectedRevision);

            try
            {
                await using var concurrentDb = new LocalDbContext();
                var concurrentlyEdited = await concurrentDb.Invoices.IgnoreQueryFilters()
                    .SingleAsync(invoice => invoice.Id == invoiceId);
                concurrentlyEdited.TaxInvoiceNumber = manualTaxInvoiceNumber;
                concurrentlyEdited.UpdatedAtUtc = newerUpdatedAtUtc;
                concurrentlyEdited.IsDirty = true;
                await concurrentDb.SaveChangesAsync();
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.True(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            db.ChangeTracker.Clear();
            var saved = await db.Invoices.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(invoice => invoice.Id == invoiceId);
            Assert.Equal(manualTaxInvoiceNumber, saved.TaxInvoiceNumber);
            Assert.Equal(newerUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.True(saved.IsDirty);
            Assert.Equal(acceptedRevision, saved.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_DelayedInvoiceNumberAck_PreservesUnsavedTrackedMemoAndAssignedNumberThroughExplicitSave()
    {
        PrepareAppRoot("georaeplan-delayed-tracked-invoice-number-ack");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var acceptedUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(1);
            var newerUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const long originalRevision = 51;
            const long acceptedRevision = 52;
            const string originalMemo = "tracked ACK 이전 전표 메모";
            const string newerMemo = "tracked ACK 대기 중 수정한 전표 메모";
            const string assignedInvoiceNumber = "S-TRACKED-ACK-0001";

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "tracked 번호 ACK 거래처",
                NameMatchKey = "tracked 번호 ACK 거래처",
                Revision = 1,
                IsDirty = false,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-2),
                UpdatedAtUtc = originalUpdatedAtUtc.AddHours(-1)
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = string.Empty,
                LocalTempNumber = "LOCAL-TRACKED-ACK-0001",
                VoucherType = VoucherType.Sales,
                InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
                Memo = originalMemo,
                Revision = originalRevision,
                IsDirty = true,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPushAckThenEmptyPullHandler(
                invoiceId,
                entityName: "Invoice",
                acceptedRevision,
                acceptedUpdatedAtUtc,
                assignedInvoiceNumber: assignedInvoiceNumber);
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            var pushedRequest = await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var pushedInvoice = Assert.Single(
                pushedRequest.Invoices,
                invoice => invoice.Id == invoiceId);
            Assert.Equal(string.Empty, pushedInvoice.InvoiceNumber);
            Assert.Equal(originalMemo, pushedInvoice.Memo);

            LocalInvoice tracked;
            try
            {
                tracked = await db.Invoices.IgnoreQueryFilters()
                    .SingleAsync(invoice => invoice.Id == invoiceId);
                tracked.Memo = newerMemo;
                tracked.UpdatedAtUtc = newerUpdatedAtUtc;
                tracked.IsDirty = true;
                db.ChangeTracker.DetectChanges();
                Assert.Equal(EntityState.Modified, db.Entry(tracked).State);
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.True(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(assignedInvoiceNumber, tracked.InvoiceNumber);
            Assert.Equal(newerMemo, tracked.Memo);
            Assert.Equal(newerUpdatedAtUtc, tracked.UpdatedAtUtc);
            Assert.True(tracked.IsDirty);
            Assert.Equal(acceptedRevision, tracked.Revision);
            Assert.Equal(EntityState.Modified, db.Entry(tracked).State);

            await db.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.Invoices.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(invoice => invoice.Id == invoiceId);
            Assert.Equal(assignedInvoiceNumber, saved.InvoiceNumber);
            Assert.Equal(newerMemo, saved.Memo);
            Assert.Equal(newerUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.True(saved.IsDirty);
            Assert.Equal(acceptedRevision, saved.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_DelayedPushAck_RestoresTrackedInvoiceRootAndLineGraphThroughExplicitSave()
    {
        PrepareAppRoot("georaeplan-delayed-tracked-invoice-line-graph");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var customerId = Guid.NewGuid();
            var invoiceId = Guid.NewGuid();
            var existingLineId = Guid.NewGuid();
            var addedLineId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var acceptedUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(1);
            var newerUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const long originalRevision = 61;
            const long acceptedRevision = 62;

            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "tracked line ACK 거래처",
                NameMatchKey = "tracked line ACK 거래처",
                Revision = 1,
                IsDirty = false,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-2),
                UpdatedAtUtc = originalUpdatedAtUtc.AddHours(-1)
            });
            db.Invoices.Add(new LocalInvoice
            {
                Id = invoiceId,
                CustomerId = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                InvoiceNumber = "S-TRACKED-LINE-0001",
                LocalTempNumber = "LOCAL-TRACKED-LINE-0001",
                VoucherType = VoucherType.Sales,
                InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
                TotalAmount = 100_000m,
                SupplyAmount = 100_000m,
                VatAmount = 0m,
                Revision = originalRevision,
                IsDirty = true,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = originalUpdatedAtUtc,
                Lines =
                [
                    new LocalInvoiceLine
                    {
                        Id = existingLineId,
                        InvoiceId = invoiceId,
                        ItemNameOriginal = "기존 품목",
                        Unit = "EA",
                        Quantity = 1m,
                        UnitPrice = 100_000m,
                        LineAmount = 100_000m,
                        OrderIndex = 1
                    }
                ]
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPushAckThenEmptyPullHandler(
                invoiceId,
                entityName: "Invoice",
                acceptedRevision,
                acceptedUpdatedAtUtc);
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            var pushedRequest = await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var pushedInvoice = Assert.Single(
                pushedRequest.Invoices,
                invoice => invoice.Id == invoiceId);
            Assert.Single(pushedInvoice.Lines);
            Assert.Equal(1m, pushedInvoice.Lines[0].Quantity);

            LocalInvoice trackedInvoice;
            LocalInvoiceLine trackedExistingLine;
            LocalInvoiceLine addedLine;
            try
            {
                trackedInvoice = await db.Invoices.IgnoreQueryFilters()
                    .Include(invoice => invoice.Lines)
                    .SingleAsync(invoice => invoice.Id == invoiceId);
                trackedExistingLine = Assert.Single(
                    trackedInvoice.Lines,
                    line => line.Id == existingLineId);
                trackedExistingLine.Quantity = 3m;
                trackedExistingLine.LineAmount = 300_000m;
                trackedExistingLine.Remark = "ACK 대기 중 수정";

                addedLine = new LocalInvoiceLine
                {
                    Id = addedLineId,
                    InvoiceId = invoiceId,
                    ItemNameOriginal = "ACK 대기 중 추가 품목",
                    Unit = "EA",
                    Quantity = 2m,
                    UnitPrice = 50_000m,
                    LineAmount = 100_000m,
                    Remark = "ACK 대기 중 추가",
                    OrderIndex = 2
                };
                trackedInvoice.Lines.Add(addedLine);
                db.Entry(addedLine).State = EntityState.Added;
                trackedInvoice.TotalAmount = 400_000m;
                trackedInvoice.SupplyAmount = 400_000m;
                trackedInvoice.UpdatedAtUtc = newerUpdatedAtUtc;
                trackedInvoice.IsDirty = true;
                db.ChangeTracker.DetectChanges();

                Assert.Equal(EntityState.Modified, db.Entry(trackedInvoice).State);
                Assert.Equal(EntityState.Modified, db.Entry(trackedExistingLine).State);
                Assert.Equal(EntityState.Added, db.Entry(addedLine).State);
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.True(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(acceptedRevision, trackedInvoice.Revision);
            Assert.Equal(newerUpdatedAtUtc, trackedInvoice.UpdatedAtUtc);
            Assert.True(trackedInvoice.IsDirty);
            Assert.Equal(EntityState.Modified, db.Entry(trackedInvoice).State);
            Assert.Equal(EntityState.Modified, db.Entry(trackedExistingLine).State);
            Assert.Equal(EntityState.Added, db.Entry(addedLine).State);
            Assert.Equal(3m, trackedExistingLine.Quantity);
            Assert.Equal(300_000m, trackedExistingLine.LineAmount);
            Assert.Contains(
                trackedInvoice.Lines,
                line => line.Id == addedLineId && line.Quantity == 2m);

            await db.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.Invoices.IgnoreQueryFilters()
                .AsNoTracking()
                .Include(invoice => invoice.Lines)
                .SingleAsync(invoice => invoice.Id == invoiceId);
            Assert.Equal(acceptedRevision, saved.Revision);
            Assert.Equal(newerUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.True(saved.IsDirty);
            Assert.Equal(400_000m, saved.TotalAmount);
            Assert.Equal(400_000m, saved.SupplyAmount);
            Assert.Contains(
                saved.Lines,
                line =>
                    line.Id == existingLineId &&
                    line.Quantity == 3m &&
                    line.LineAmount == 300_000m &&
                    line.Remark == "ACK 대기 중 수정");
            Assert.Contains(
                saved.Lines,
                line =>
                    line.Id == addedLineId &&
                    line.Quantity == 2m &&
                    line.LineAmount == 100_000m &&
                    line.Remark == "ACK 대기 중 추가");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ClearStaleDirtyEntities_SameRevisionButDifferentPayload_KeepsDirty()
    {
        PrepareAppRoot("georaeplan-stale-dirty-payload-mismatch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var createdAtUtc = DateTime.UtcNow.AddHours(-1);
            var localUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-5);
            const long revision = 12;
            const string localName = "서버와 다른 최신 로컬 단위";

            var local = new LocalUnit
            {
                Id = unitId,
                Name = localName,
                IsActive = true,
                IsDeleted = false,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = localUpdatedAtUtc,
                Revision = revision,
                IsDirty = true
            };
            db.Units.Add(local);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var changed = await InvokeClearStaleDirtyEntitiesAsync<LocalUnit, UnitDto>(
                sync,
                [local],
                [
                    new UnitDto
                    {
                        Id = unitId,
                        Name = "서버에 남은 이전 단위",
                        IsActive = true,
                        IsDeleted = false,
                        CreatedAtUtc = createdAtUtc,
                        UpdatedAtUtc = localUpdatedAtUtc,
                        Revision = revision
                    }
                ]);

            Assert.Equal(0, changed);
            db.ChangeTracker.Clear();
            var saved = await db.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(localName, saved.Name);
            Assert.Equal(revision, saved.Revision);
            Assert.Equal(localUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.False(saved.IsDeleted);
            Assert.True(saved.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ClearStaleDirtyEntities_EquivalentPayload_CleansAndAppliesServerRevisionAndTimestamp()
    {
        PrepareAppRoot("georaeplan-stale-dirty-payload-match");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var createdAtUtc = DateTime.UtcNow.AddHours(-1);
            var localUpdatedAtUtc = new DateTime(
                DateTime.UtcNow.AddMinutes(-5).Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond,
                DateTimeKind.Utc);
            var serverUpdatedAtUtc = localUpdatedAtUtc.AddMilliseconds(500);
            const long localRevision = 25;
            const long serverRevision = 25;
            const string matchingName = "서버와 일치하는 단위";

            var local = new LocalUnit
            {
                Id = unitId,
                Name = matchingName,
                IsActive = true,
                IsDeleted = false,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = localUpdatedAtUtc,
                Revision = localRevision,
                IsDirty = true
            };
            db.Units.Add(local);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var sync = CreateSyncService(db, session);
            var changed = await InvokeClearStaleDirtyEntitiesAsync<LocalUnit, UnitDto>(
                sync,
                [local],
                [
                    new UnitDto
                    {
                        Id = unitId,
                        Name = matchingName,
                        IsActive = true,
                        IsDeleted = false,
                        CreatedAtUtc = createdAtUtc,
                        UpdatedAtUtc = serverUpdatedAtUtc,
                        Revision = serverRevision
                    }
                ]);

            Assert.Equal(1, changed);
            db.ChangeTracker.Clear();
            var saved = await db.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(matchingName, saved.Name);
            Assert.Equal(serverRevision, saved.Revision);
            Assert.Equal(serverUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.False(saved.IsDeleted);
            Assert.False(saved.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ClearStaleDirtyEntities_TrackedUnsavedEdit_RebasesOnlyRevisionAndRestoresForExplicitSave()
    {
        PrepareAppRoot("georaeplan-stale-dirty-tracked-unsaved-rebase");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var createdAtUtc = DateTime.UtcNow.AddHours(-1);
            var persistedUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var newerUpdatedAtUtc = persistedUpdatedAtUtc.AddMinutes(2);
            const long persistedRevision = 71;
            const long serverRevision = 72;
            const string persistedName = "stale 조회 당시 단위";
            const string newerName = "stale 조회 후 미저장 수정 단위";

            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = persistedName,
                IsActive = true,
                IsDeleted = false,
                CreatedAtUtc = createdAtUtc,
                UpdatedAtUtc = persistedUpdatedAtUtc,
                Revision = persistedRevision,
                IsDirty = true
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var dirtySnapshot = await db.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            var tracked = await db.Units.IgnoreQueryFilters()
                .SingleAsync(unit => unit.Id == unitId);
            tracked.Name = newerName;
            tracked.UpdatedAtUtc = newerUpdatedAtUtc;
            tracked.IsDirty = true;
            db.ChangeTracker.DetectChanges();
            Assert.Equal(EntityState.Modified, db.Entry(tracked).State);

            using var sync = CreateSyncService(db, session);
            var changed = await InvokeClearStaleDirtyEntitiesAsync<LocalUnit, UnitDto>(
                sync,
                [dirtySnapshot],
                [
                    new UnitDto
                    {
                        Id = unitId,
                        Name = persistedName,
                        IsActive = true,
                        IsDeleted = false,
                        CreatedAtUtc = createdAtUtc,
                        UpdatedAtUtc = persistedUpdatedAtUtc,
                        Revision = serverRevision
                    }
                ]);

            Assert.Equal(0, changed);

            await using (var persistedBeforeSaveDb = new LocalDbContext())
            {
                var persistedBeforeSave = await persistedBeforeSaveDb.Units
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(unit => unit.Id == unitId);
                Assert.Equal(persistedName, persistedBeforeSave.Name);
                Assert.Equal(persistedUpdatedAtUtc, persistedBeforeSave.UpdatedAtUtc);
                Assert.True(persistedBeforeSave.IsDirty);
                Assert.Equal(serverRevision, persistedBeforeSave.Revision);
            }

            InvokeRestoreTrackedMutationsPreservedDuringSync(sync);

            Assert.Equal(newerName, tracked.Name);
            Assert.Equal(newerUpdatedAtUtc, tracked.UpdatedAtUtc);
            Assert.True(tracked.IsDirty);
            Assert.Equal(serverRevision, tracked.Revision);
            Assert.Equal(EntityState.Modified, db.Entry(tracked).State);

            await db.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(newerName, saved.Name);
            Assert.Equal(newerUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.True(saved.IsDirty);
            Assert.Equal(serverRevision, saved.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_DelayedPushException_RestoresUnsavedTrackedUnitForExplicitSave()
    {
        PrepareAppRoot("georaeplan-delayed-push-exception-tracked-unit");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var newerUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const long revision = 81;
            const string originalName = "push 예외 이전 단위";
            const string newerName = "push 예외 대기 중 미저장 수정 단위";

            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = originalName,
                IsActive = true,
                Revision = revision,
                IsDirty = true,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var handler = new DelayedPushAckThenEmptyPullHandler(
                unitId,
                entityName: "Unit",
                acceptedRevision: revision,
                acceptedUpdatedAtUtc: originalUpdatedAtUtc,
                pushException: new InvalidOperationException("simulated delayed push failure"));
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            var pushedRequest = await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var pushedUnit = Assert.Single(
                pushedRequest.Units,
                unit => unit.Id == unitId);
            Assert.Equal(originalName, pushedUnit.Name);

            LocalUnit tracked;
            try
            {
                tracked = await db.Units.IgnoreQueryFilters()
                    .SingleAsync(unit => unit.Id == unitId);
                tracked.Name = newerName;
                tracked.UpdatedAtUtc = newerUpdatedAtUtc;
                tracked.IsDirty = true;
                db.ChangeTracker.DetectChanges();
                Assert.Equal(EntityState.Modified, db.Entry(tracked).State);
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.False(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(newerName, tracked.Name);
            Assert.Equal(newerUpdatedAtUtc, tracked.UpdatedAtUtc);
            Assert.True(tracked.IsDirty);
            Assert.Equal(revision, tracked.Revision);
            Assert.Equal(EntityState.Modified, db.Entry(tracked).State);

            await db.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(newerName, saved.Name);
            Assert.Equal(newerUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.True(saved.IsDirty);
            Assert.Equal(revision, saved.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_PushBoundary_PreservesAlreadyModifiedNonRequestUnit()
    {
        PrepareAppRoot("georaeplan-preexisting-non-request-unit");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var unitId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            var modifiedUpdatedAtUtc = originalUpdatedAtUtc.AddMinutes(2);
            const string modifiedName = "non-request unit modified before push boundary";

            db.Units.Add(new LocalUnit
            {
                Id = unitId,
                Name = "non-request unit before edit",
                IsActive = true,
                Revision = 15,
                IsDirty = false,
                CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                UpdatedAtUtc = originalUpdatedAtUtc
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var trackedUnit = await db.Units.IgnoreQueryFilters()
                .SingleAsync(unit => unit.Id == unitId);
            trackedUnit.Name = modifiedName;
            trackedUnit.UpdatedAtUtc = modifiedUpdatedAtUtc;
            trackedUnit.IsDirty = true;
            db.ChangeTracker.DetectChanges();
            Assert.Equal(EntityState.Modified, db.Entry(trackedUnit).State);

            using var sync = CreateSyncService(db, session);
            var baseline = InvokeCaptureTrackedStateBeforePush(sync);
            InvokeCaptureNonMutationTrackedChangesAtPushBoundary(
                sync,
                baseline,
                includeExistingChanges: true);

            Assert.Equal(EntityState.Detached, db.Entry(trackedUnit).State);
            db.ChangeTracker.Clear();
            InvokeRestoreTrackedMutationsPreservedDuringSync(sync);

            Assert.Equal(modifiedName, trackedUnit.Name);
            Assert.Equal(modifiedUpdatedAtUtc, trackedUnit.UpdatedAtUtc);
            Assert.True(trackedUnit.IsDirty);
            Assert.Equal(15, trackedUnit.Revision);
            Assert.Equal(EntityState.Modified, db.Entry(trackedUnit).State);

            await db.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == unitId);
            Assert.Equal(modifiedName, saved.Name);
            Assert.Equal(modifiedUpdatedAtUtc, saved.UpdatedAtUtc);
            Assert.True(saved.IsDirty);
            Assert.Equal(15, saved.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncService_PreexistingModifiedUnit_IsNotCommittedBeforeDelayedPushException()
    {
        PrepareAppRoot("georaeplan-delayed-non-request-added-unit-failure");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var preparedUnitId = Guid.NewGuid();
            var modifiedUnitId = Guid.NewGuid();
            var originalUpdatedAtUtc = DateTime.UtcNow.AddMinutes(-10);
            const long revision = 91;
            const string originalName = "non-request unit before push";
            const string modifiedName = "unit edited before failed push";

            db.Units.AddRange(
                new LocalUnit
                {
                    Id = preparedUnitId,
                    Name = "prepared unit before failed push",
                    IsActive = true,
                    Revision = revision,
                    IsDirty = true,
                    CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                    UpdatedAtUtc = originalUpdatedAtUtc
                },
                new LocalUnit
                {
                    Id = modifiedUnitId,
                    Name = originalName,
                    IsActive = true,
                    Revision = 17,
                    IsDirty = false,
                    CreatedAtUtc = originalUpdatedAtUtc.AddHours(-1),
                    UpdatedAtUtc = originalUpdatedAtUtc
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var modifiedUnit = await db.Units.IgnoreQueryFilters()
                .SingleAsync(unit => unit.Id == modifiedUnitId);
            modifiedUnit.Name = modifiedName;
            db.ChangeTracker.DetectChanges();
            Assert.False(modifiedUnit.IsDirty);
            Assert.Equal(EntityState.Modified, db.Entry(modifiedUnit).State);

            var handler = new DelayedPushAckThenEmptyPullHandler(
                preparedUnitId,
                entityName: "Unit",
                acceptedRevision: revision,
                acceptedUpdatedAtUtc: originalUpdatedAtUtc,
                pushException: new InvalidOperationException("simulated delayed push failure"));
            using var sync = CreateSyncService(db, session, handler);

            var syncTask = sync.TrySyncAsync();
            try
            {
                var pushedRequest = await handler.PushReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Single(pushedRequest.Units, unit => unit.Id == preparedUnitId);
                Assert.DoesNotContain(pushedRequest.Units, unit => unit.Id == modifiedUnitId);

                await using var beforeExplicitSaveDb = new LocalDbContext();
                var persistedBeforeExplicitSave = await beforeExplicitSaveDb.Units
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(unit => unit.Id == modifiedUnitId);
                Assert.Equal(originalName, persistedBeforeExplicitSave.Name);
                Assert.False(persistedBeforeExplicitSave.IsDirty);
            }
            finally
            {
                handler.ReleasePush();
            }

            Assert.False(await syncTask.WaitAsync(TimeSpan.FromSeconds(15)));

            Assert.Equal(modifiedName, modifiedUnit.Name);
            Assert.Equal(originalUpdatedAtUtc, modifiedUnit.UpdatedAtUtc);
            Assert.True(modifiedUnit.IsDirty);
            Assert.Equal(17, modifiedUnit.Revision);
            Assert.Equal(EntityState.Modified, db.Entry(modifiedUnit).State);

            await db.SaveChangesAsync();

            await using var verificationDb = new LocalDbContext();
            var saved = await verificationDb.Units.IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(unit => unit.Id == modifiedUnitId);
            Assert.Equal(modifiedName, saved.Name);
            Assert.True(saved.UpdatedAtUtc > originalUpdatedAtUtc);
            Assert.True(saved.IsDirty);
            Assert.Equal(17, saved.Revision);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static ServiceProvider BuildRuntimeProvider(
        SessionState session,
        HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddDbContext<LocalDbContext>();
        services.AddSingleton(session);
        services.AddSingleton<OfficeAccessService>();
        services.AddSingleton<SyncRequestDispatcher>();
        services.AddSingleton<DesktopDataChangeNotifier>();
        services.AddScoped<SyncDiagnosticsService>();
        services.AddScoped<LocalStateService>();
        services.AddScoped<RentalStateService>();
        services.AddScoped(_ => new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://localhost")
        });
        services.AddScoped<ErpApiClient>();
        services.AddScoped<SyncService>();
        return services.BuildServiceProvider();
    }

    private static Task InvokePushPreparedRequestAsync(
        SyncService sync,
        ErpApiClient api,
        SessionState session,
        SyncPushRequest request,
        string? businessDatabaseName,
        IReadOnlyCollection<(string EntityName, Guid EntityId)>?
            dependencyOnlyKeys)
    {
        var keyType = typeof(SyncService).GetNestedType(
            "SyncEntityKey",
            BindingFlags.NonPublic)
            ?? throw new MissingMemberException(
                nameof(SyncService),
                "SyncEntityKey");
        object? keySet = null;
        if (dependencyOnlyKeys is not null)
        {
            var setType = typeof(HashSet<>).MakeGenericType(keyType);
            keySet = Activator.CreateInstance(setType)
                ?? throw new InvalidOperationException(
                    "Could not create dependency key set.");
            var addMethod = setType.GetMethod("Add")
                ?? throw new MissingMethodException(setType.Name, "Add");
            foreach (var (entityName, entityId) in dependencyOnlyKeys)
            {
                var normalizedEntityName =
                    entityName.StartsWith("Local", StringComparison.Ordinal)
                        ? entityName["Local".Length..]
                        : entityName;
                if (string.Equals(
                        normalizedEntityName,
                        "Transaction",
                        StringComparison.Ordinal))
                {
                    normalizedEntityName = "TransactionRecord";
                }

                var key = Activator.CreateInstance(
                    keyType,
                    BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic,
                    binder: null,
                    args: [normalizedEntityName, entityId],
                    culture: null)
                    ?? throw new InvalidOperationException(
                        "Could not create dependency key.");
                addMethod.Invoke(keySet, [key]);
            }
        }

        var method = typeof(SyncService).GetMethod(
            "PushPreparedRequestAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "PushPreparedRequestAsync");
        return (Task)method.Invoke(
            sync,
            [
                api,
                session,
                request,
                businessDatabaseName,
                keySet,
                CancellationToken.None
            ])!;
    }

    private static string InvokeComputePreparedMutationPayloadHash(
        string entityName,
        SyncEntityDto entity)
    {
        var method = typeof(SyncService).GetMethod(
            "ComputePreparedMutationPayloadHash",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "ComputePreparedMutationPayloadHash");
        return (string)method.Invoke(null, [entityName, entity])!;
    }

    private static LocalSyncOutboxEntry CreateOutboxEntry(
        string status,
        string entityName = nameof(LocalCustomer),
        Guid? entityId = null,
        Guid? entryId = null,
        string tenantCode = TenantScopeCatalog.UsenetGroup,
        string officeCode = OfficeCodeCatalog.Usenet,
        string responsibleOfficeCode = OfficeCodeCatalog.Usenet,
        DateTime? sentAtUtc = null)
        => new()
        {
            Id = entryId ?? Guid.NewGuid(),
            MutationId = $"test-device:{entityName}:{(entityId ?? Guid.NewGuid()):N}:1:{DateTime.UtcNow.Ticks}:0",
            DeviceId = "test-device",
            EntityName = entityName,
            EntityId = entityId ?? Guid.NewGuid(),
            ExpectedRevision = 1,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            Status = status,
            PreparedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            SentAtUtc = sentAtUtc ?? DateTime.UtcNow
        };

    private static LocalDeferredRecycleBinPurgeRecord
        CreateDeferredPurgeRecord(
            Guid receiptId,
            string kind,
            Guid entityId,
            long revision,
            string businessDatabaseName,
            DateTime now)
        => new()
        {
            Id = receiptId,
            BusinessDatabaseName =
                businessDatabaseName,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode =
                OfficeCodeCatalog.Usenet,
            Kind = kind,
            EntityId = entityId,
            Revision = revision,
            PurgedAtUtc = now,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

    private static LocalCustomer CreateDeferredPurgeCustomer(
        Guid customerId,
        DateTime now,
        bool isDeleted = false)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = $"deferred purge customer {customerId:N}",
            NameMatchKey = $"DEFERREDPURGECUSTOMER{customerId:N}",
            Revision = 5,
            IsDirty = false,
            IsDeleted = isDeleted,
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now
        };

    private static LocalInvoice CreateDeferredPurgeInvoice(
        Guid invoiceId,
        Guid customerId,
        DateTime now,
        bool isDeleted = false)
        => new()
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 7, 31),
            VersionGroupId = invoiceId,
            IsLatestVersion = true,
            IsConfirmed = true,
            Revision = 5,
            IsDirty = false,
            IsDeleted = isDeleted,
            CreatedAtUtc = now.AddHours(-1),
            UpdatedAtUtc = now
        };

    private static LocalSyncOutboxEntry CreateDeferredPurgeOutbox(
        Guid outboxId,
        string entityName,
        Guid entityId,
        long expectedRevision,
        string businessDatabaseName,
        SessionState session,
        DateTime now)
        => new()
        {
            Id = outboxId,
            MutationId =
                $"deferred-purge:{entityName}:{entityId:N}:{expectedRevision}",
            DeviceId = "deferred-purge-test-device",
            EntityName = entityName,
            EntityId = entityId,
            ExpectedRevision = expectedRevision,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BusinessDatabaseName = businessDatabaseName,
            SessionId = session.SessionId,
            UserId = session.User!.UserId,
            Status = "Failed",
            ErrorMessage = "test pending exact-scope mutation",
            PreparedAtUtc = now
        };

    private static LocalSyncOutboxEntry CreateScopedOutbox(
        string mutationId,
        Guid entityId,
        string status,
        DateTime preparedAtUtc,
        SessionState session,
        string tenantCode = TenantScopeCatalog.UsenetGroup,
        string officeCode = OfficeCodeCatalog.Usenet,
        string responsibleOfficeCode = OfficeCodeCatalog.Usenet,
        string businessDatabaseName = "USENET",
        string deviceId = "test-device",
        Guid? sessionId = null,
        Guid? userId = null)
        => new()
        {
            Id = Guid.NewGuid(),
            MutationId = mutationId,
            DeviceId = deviceId,
            EntityName = nameof(LocalItem),
            EntityId = entityId,
            ExpectedRevision = 1,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            BusinessDatabaseName = businessDatabaseName,
            SessionId = sessionId ?? session.SessionId,
            UserId = userId ?? session.User!.UserId,
            Status = status,
            PreparedAtUtc = preparedAtUtc
        };

    private static Task<string> ReadOutboxStatusAsync(LocalDbContext db)
        => db.SyncOutboxEntries
            .AsNoTracking()
            .Select(entry => entry.Status)
            .SingleAsync();

    private static Task InvokeRetryDeferredPurgeRecordsAsync(
        SyncService sync)
    {
        var method = typeof(SyncService).GetMethod(
            "RetryDeferredPurgeRecordsAsync",
            BindingFlags.Instance |
            BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "RetryDeferredPurgeRecordsAsync");
        return (Task)method.Invoke(
            sync,
            [CancellationToken.None])!;
    }

    private static Task<string> ReadOutboxStatusAsync(LocalDbContext db, Guid entryId)
        => db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry => entry.Id == entryId)
            .Select(entry => entry.Status)
            .SingleAsync();

    private static async Task<int> InvokeMarkOutboxAcknowledgedForCleanEntitiesAsync<TLocal, TDto>(
        SyncService sync,
        IReadOnlyCollection<TDto> serverEntities,
        SessionState session,
        string deviceId,
        string businessDatabaseName)
        where TLocal : class, ILocalSyncEntity
        where TDto : SyncEntityDto
    {
        var method = typeof(SyncService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(info =>
                info.Name == "MarkOutboxAcknowledgedForCleanEntitiesAsync" &&
                info.IsGenericMethodDefinition &&
                info.GetGenericArguments().Length == 2);
        var generic = method.MakeGenericMethod(typeof(TLocal), typeof(TDto));
        var task = (Task<int>)generic.Invoke(
            sync,
            [
                serverEntities,
                session,
                deviceId,
                businessDatabaseName,
                CancellationToken.None
            ])!;
        return await task;
    }

    private static async Task<int> InvokeClearStaleDirtyEntitiesAsync<TLocal, TDto>(
        SyncService sync,
        IReadOnlyCollection<TLocal> dirtyEntities,
        IReadOnlyCollection<TDto> serverEntities)
        where TLocal : class, ILocalSyncEntity
        where TDto : SyncEntityDto
    {
        var method = typeof(SyncService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(info =>
                info.Name == "ClearStaleDirtyEntitiesAsync" &&
                info.IsGenericMethodDefinition &&
                info.GetGenericArguments().Length == 2);
        var generic = method.MakeGenericMethod(typeof(TLocal), typeof(TDto));
        var task = (Task<int>)generic.Invoke(
            sync,
            [dirtyEntities, serverEntities, CancellationToken.None])!;
        return await task;
    }

    private static void InvokeRestoreTrackedMutationsPreservedDuringSync(
        SyncService sync)
    {
        var method = typeof(SyncService).GetMethod(
            "RestoreTrackedMutationsPreservedDuringSync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "RestoreTrackedMutationsPreservedDuringSync");
        method.Invoke(sync, null);
    }

    private static object InvokeCaptureTrackedStateBeforePush(SyncService sync)
    {
        var method = typeof(SyncService).GetMethod(
            "CaptureTrackedStateBeforePush",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "CaptureTrackedStateBeforePush");
        return method.Invoke(sync, null)
               ?? throw new InvalidOperationException(
                   "CaptureTrackedStateBeforePush returned null.");
    }

    private static void InvokeCaptureNonMutationTrackedChangesAtPushBoundary(
        SyncService sync,
        object baseline,
        bool includeExistingChanges)
    {
        var method = typeof(SyncService).GetMethod(
            "CaptureNonMutationTrackedChangesAtPushBoundary",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "CaptureNonMutationTrackedChangesAtPushBoundary");
        method.Invoke(sync, [baseline, includeExistingChanges]);
    }

    private static void InvokeCaptureTrackedChangesBeforePreparedMutationBoundary(
        SyncService sync,
        SyncPushRequest preparedRequest)
    {
        var snapshotBuilder = typeof(SyncService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(info =>
                info.Name == "BuildPreparedMutationSnapshots" &&
                info.GetParameters().Length == 2);
        var preparedMutationSnapshots = snapshotBuilder.Invoke(
            sync,
            [preparedRequest, null]);
        var method = typeof(SyncService).GetMethod(
            "CaptureTrackedChangesBeforePreparedMutationBoundary",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "CaptureTrackedChangesBeforePreparedMutationBoundary");
        method.Invoke(sync, [preparedMutationSnapshots]);
    }

    private static Task InvokeApplyAcceptedRevisionsAsync(
        SyncService sync,
        IReadOnlyCollection<SyncAcceptedRevisionDto> acceptedRevisions,
        SyncPushRequest preparedRequest)
    {
        var snapshotBuilder = typeof(SyncService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(info =>
                info.Name == "BuildPreparedMutationSnapshots" &&
                info.GetParameters().Length == 2);
        var preparedMutationSnapshots = snapshotBuilder.Invoke(
            sync,
            [preparedRequest, null]);

        var method = typeof(SyncService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(info =>
                info.Name == "ApplyAcceptedRevisionsAsync" &&
                !info.IsGenericMethodDefinition &&
                info.GetParameters().Length == 3);
        return (Task)method.Invoke(
            sync,
            [acceptedRevisions, preparedMutationSnapshots, CancellationToken.None])!;
    }

    private static Task InvokeMarkOutboxAcknowledgedAsync(
        SyncService sync,
        SyncPushRequest request,
        IReadOnlyCollection<SyncAcceptedRevisionDto> acceptedRevisions)
    {
        var method = typeof(SyncService).GetMethod(
            "MarkOutboxAcknowledgedAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SyncService), "MarkOutboxAcknowledgedAsync");
        return (Task)method.Invoke(sync, [request, acceptedRevisions, CancellationToken.None])!;
    }

    private static Task InvokeApplyPullAsync(SyncService sync, SyncPullResponse pull)
    {
        var method = typeof(SyncService).GetMethod("ApplyPullAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SyncService), "ApplyPullAsync");
        return (Task)method.Invoke(
            sync,
            [
                pull,
                0L,
                CancellationToken.None,
                false
            ])!;
    }

    private static Task InvokeApplyPullAndUpdateRevisionAsync(
        SyncService sync,
        SyncPullResponse pull,
        long sinceRevision)
    {
        var method = typeof(SyncService).GetMethod(
            "ApplyPullAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SyncService), "ApplyPullAsync");
        return (Task)method.Invoke(
            sync,
            [
                pull,
                sinceRevision,
                CancellationToken.None,
                true
            ])!;
    }

    private static Task<IReadOnlyList<LocalSyncOutboxEntry>>
        InvokeLoadEligibleOutboxReconciliationCandidatesAsync(
            SyncService sync)
    {
        var method = typeof(SyncService).GetMethod(
            "LoadEligibleOutboxReconciliationCandidatesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "LoadEligibleOutboxReconciliationCandidatesAsync");
        return (Task<IReadOnlyList<LocalSyncOutboxEntry>>)method.Invoke(
            sync,
            [CancellationToken.None])!;
    }

    private static Task<bool> InvokeHasPendingOutboxForSessionAsync(
        SyncService sync,
        SessionState session)
    {
        var method = typeof(SyncService).GetMethod(
            "HasPendingOutboxForSessionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "HasPendingOutboxForSessionAsync");
        return (Task<bool>)method.Invoke(
            sync,
            [session, CancellationToken.None])!;
    }

    private static Task<IReadOnlyList<DirtyOfficeSummary>>
        InvokeGetPendingReconciliationOfficeSummariesAsync(
            SyncService sync)
    {
        var method = typeof(SyncService).GetMethod(
            "GetPendingReconciliationOfficeSummariesOutsideCurrentSessionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "GetPendingReconciliationOfficeSummariesOutsideCurrentSessionAsync");
        return (Task<IReadOnlyList<DirtyOfficeSummary>>)method.Invoke(
            sync,
            [CancellationToken.None])!;
    }

    private static Task InvokeUpsertPulledRentalManagementCompaniesAsync(
        SyncService sync,
        IReadOnlyList<RentalManagementCompanyDto> companies)
    {
        var method = typeof(SyncService).GetMethod(
            "UpsertPulledRentalManagementCompaniesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "UpsertPulledRentalManagementCompaniesAsync");
        return (Task)method.Invoke(
            sync,
            [
                companies,
                CancellationToken.None
            ])!;
    }

    private static RentalManagementCompanyDto CreatePulledRentalManagementCompany(
        Guid id,
        string code,
        string name,
        long revision,
        DateTime updatedAtUtc,
        bool isDeleted = false)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.Itworld,
            Code = code,
            Name = name,
            IsActive = !isDeleted,
            IsDeleted = isDeleted,
            Revision = revision,
            CreatedAtUtc = updatedAtUtc.AddHours(-4),
            UpdatedAtUtc = updatedAtUtc
        };

    private static Task InvokeUpsertPulledItemsAsync(SyncService sync, SyncPullResponse pull)
    {
        var method = typeof(SyncService).GetMethod(
            "UpsertPulledItemsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SyncService), "UpsertPulledItemsAsync");
        return (Task)method.Invoke(
            sync,
            [
                pull.Items,
                CancellationToken.None,
                false
            ])!;
    }

    private static Task<bool> InvokePullNewCoreAsync(
        SyncService sync,
        bool rejectPulledDirtyCollisions)
    {
        var method = typeof(SyncService).GetMethod(
            "PullNewCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SyncService), "PullNewCoreAsync");
        return (Task<bool>)method.Invoke(
            sync,
            [rejectPulledDirtyCollisions, CancellationToken.None])!;
    }

    private static Task<bool> InvokeTryRefreshSharedMirrorCoreAsync(
        SyncService sync)
    {
        var method = typeof(SyncService).GetMethod(
            "TryRefreshSharedMirrorCoreAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "TryRefreshSharedMirrorCoreAsync");
        return (Task<bool>)method.Invoke(sync, [CancellationToken.None, true])!;
    }

    private static Task InvokeUpsertPulledUnitsAsync(
        SyncService sync,
        IReadOnlyList<UnitDto> units)
        => InvokePrivatePullUpsertAsync(sync, "UpsertPulledUnitsAsync", units);

    private static Task InvokeUpsertPulledRentalBillingProfilesAsync(
        SyncService sync,
        IReadOnlyList<RentalBillingProfileDto> profiles)
        => InvokePrivatePullUpsertAsync(
            sync,
            "UpsertPulledRentalBillingProfilesAsync",
            profiles);

    private static Task InvokeUpsertPulledRentalAssetsAsync(
        SyncService sync,
        IReadOnlyList<RentalAssetDto> assets)
        => InvokePrivatePullUpsertAsync(
            sync,
            "UpsertPulledRentalAssetsAsync",
            assets);

    private static Task InvokePrivatePullUpsertAsync<T>(
        SyncService sync,
        string methodName,
        IReadOnlyList<T> dtos)
    {
        var method = typeof(SyncService).GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(SyncService), methodName);
        return (Task)method.Invoke(sync, [dtos, CancellationToken.None])!;
    }

    private static LocalRentalBillingProfile CreateTestRentalBillingProfile(
        Guid id,
        string profileKey,
        bool isDirty,
        DateTime now)
    {
        var local = LocalMappings.ToLocal(CreateTestRentalBillingProfileDto(
            id,
            profileKey,
            revision: 3,
            now));
        local.IsDirty = isDirty;
        return local;
    }

    private static RentalBillingProfileDto CreateTestRentalBillingProfileDto(
        Guid id,
        string profileKey,
        long revision,
        DateTime now)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            ProfileKey = profileKey,
            CustomerName = profileKey,
            ItemName = "canonical profile item",
            BillingType = "개별",
            BillingAdvanceMode = "후불",
            BillingMethod = "전자세금계산서",
            BillingDay = 28,
            BillingCycleMonths = 1,
            MonthlyAmount = 10_000m,
            IsActive = true,
            Revision = revision,
            CreatedAtUtc = now.AddHours(-2),
            UpdatedAtUtc = now
        };

    private static LocalRentalAsset CreateTestRentalAsset(
        Guid id,
        string assetKey,
        bool isDirty,
        DateTime now)
    {
        var local = LocalMappings.ToLocal(CreateTestRentalAssetDto(
            id,
            assetKey,
            revision: 3,
            now));
        local.IsDirty = isDirty;
        return local;
    }

    private static RentalAssetDto CreateTestRentalAssetDto(
        Guid id,
        string assetKey,
        long revision,
        DateTime now)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = OfficeCodeCatalog.Usenet,
            AssetKey = assetKey,
            ManagementId = $"MID-{assetKey}",
            ManagementNumber = $"MNO-{assetKey}",
            ItemName = "canonical asset item",
            AssetStatus = "임대진행중",
            Revision = revision,
            CreatedAtUtc = now.AddHours(-2),
            UpdatedAtUtc = now
        };

    private static void InvokeStampOutgoingMutations(
        SyncPushRequest request,
        string deviceId,
        string businessDatabaseName)
    {
        var method = typeof(SyncService)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(info =>
                info.Name == "StampOutgoingMutations" &&
                !info.IsGenericMethodDefinition &&
                info.GetParameters().Length == 3);
        method.Invoke(null, [request, deviceId, businessDatabaseName]);
    }

    private static Task InvokeRecordPreparedMutationsAsync(
        SyncService sync,
        SyncPushRequest request,
        SessionState session)
    {
        var method = typeof(SyncService).GetMethod(
            "RecordPreparedMutationsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                nameof(SyncService),
                "RecordPreparedMutationsAsync");
        return (Task)method.Invoke(
            sync,
            [request, session, null, null, CancellationToken.None])!;
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetSession(
            "test-token",
            new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = "outbox-admin",
                Role = "Admin",
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            },
            DateTime.UtcNow.AddDays(1));
        return session;
    }

    private static SessionState CreateOfficeSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = $"{officeCode.ToLowerInvariant()}-user",
            Role = DomainConstants.RoleUser,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly
        });
        return session;
    }

    private static SessionState CreateOnlineOfficeSession(string tenantCode, string officeCode)
    {
        var session = new SessionState();
        session.SetSession(
            "test-office-token",
            new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = $"{officeCode.ToLowerInvariant()}-online-user",
                Role = DomainConstants.RoleUser,
                TenantCode = tenantCode,
                OfficeCode = officeCode,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly
            },
            DateTime.UtcNow.AddDays(1));
        return session;
    }

    private static SyncService CreateSyncService(LocalDbContext db, SessionState session)
        => CreateSyncService(db, session, handler: null);

    private static SyncService CreateSyncService(
        LocalDbContext db,
        SessionState session,
        HttpMessageHandler? handler,
        DesktopDataChangeNotifier? notifier = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        var dispatcher = new SyncRequestDispatcher();
        var local = notifier is null
            ? new LocalStateService(db, new OfficeAccessService(), dispatcher, session)
            : new LocalStateService(db, new OfficeAccessService(), dispatcher, session, notifier);
        var rental = new RentalStateService(db, local);
        var diagnostics = new SyncDiagnosticsService(session);
        var httpClient = handler is null
            ? new HttpClient()
            : new HttpClient(handler);
        httpClient.BaseAddress = new Uri("http://localhost/");
        var api = new ErpApiClient(httpClient, session);
        return new SyncService(
            db,
            local,
            rental,
            api,
            session,
            dispatcher,
            diagnostics,
            httpClientFactory: httpClientFactory);
    }

    private sealed class AuthoritativePullOnlyHandler(SyncPullResponse response)
        : HttpMessageHandler
    {
        public int PullCount { get; private set; }

        public int PushCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/sync/push")
            {
                PushCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult())
                });
            }

            if (path == "/sync/pull")
            {
                PullCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(response)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ForbiddenPushThenEmptyPullHandler : HttpMessageHandler
    {
        private readonly string _message;

        public ForbiddenPushThenEmptyPullHandler(string message)
        {
            _message = message;
        }

        public int PushCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/sync/push")
            {
                PushCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = JsonContent.Create(new { message = _message })
                });
            }

            if (path == "/sync/pull")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPullResponse())
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class OutboxReconciliationEchoHandler(CustomerDto customer)
        : HttpMessageHandler
    {
        public int PullCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/sync/push")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult())
                });
            }

            if (path == "/sync/pull")
            {
                PullCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPullResponse
                    {
                        Customers = [customer],
                        CurrentServerRevision = customer.Revision
                    })
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class OutboxReconciliationSequenceHandler(
        IReadOnlyList<CustomerDto> firstPullCustomers,
        IReadOnlyList<CustomerDto> subsequentPullCustomers)
        : HttpMessageHandler
    {
        public int PullCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/sync/push")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult())
                });
            }

            if (path == "/sync/pull")
            {
                PullCount++;
                var customers = PullCount == 1
                    ? firstPullCustomers
                    : subsequentPullCustomers;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPullResponse
                    {
                        Customers = customers.ToList(),
                        CurrentServerRevision = customers.Count == 0
                            ? 0
                            : customers.Max(customer => customer.Revision)
                    })
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StoredOfficeOutboxReconciliationScenario
    {
        private readonly UserSessionDto _storedUser;
        private readonly CustomerDto _serverCustomer;

        public StoredOfficeOutboxReconciliationScenario(
            UserSessionDto storedUser,
            CustomerDto serverCustomer)
        {
            _storedUser = storedUser;
            _serverCustomer = serverCustomer;
            MainHandler = new MainScenarioHandler(this);
            OfficeHttpClientFactory = new ScenarioHttpClientFactory(this);
        }

        public HttpMessageHandler MainHandler { get; }

        public IHttpClientFactory OfficeHttpClientFactory { get; }

        public int LoginCount { get; private set; }

        public int OfficePullCount { get; private set; }

        private sealed class MainScenarioHandler(
            StoredOfficeOutboxReconciliationScenario owner)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path == "/auth/login")
                {
                    owner.LoginCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new LoginResponse
                        {
                            Token = "stored-office-test-token",
                            ExpiresAtUtc = DateTime.UtcNow.AddDays(1),
                            User = owner._storedUser
                        })
                    });
                }

                if (path == "/sync/push")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new SyncPushResult())
                    });
                }

                if (path == "/sync/pull")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new SyncPullResponse
                        {
                            CurrentServerRevision = owner._serverCustomer.Revision
                        })
                    });
                }

                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }

        private sealed class ScenarioHttpClientFactory(
            StoredOfficeOutboxReconciliationScenario owner)
            : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
                => new(new OfficeScenarioHandler(owner));
        }

        private sealed class OfficeScenarioHandler(
            StoredOfficeOutboxReconciliationScenario owner)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path == "/sync/pull")
                {
                    owner.OfficePullCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new SyncPullResponse
                        {
                            Customers = [owner._serverCustomer],
                            CurrentServerRevision = owner._serverCustomer.Revision
                        })
                    });
                }

                if (path == "/sync/push")
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new SyncPushResult())
                    });
                }

                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.NotFound));
            }
        }
    }

    private sealed class OutboxTransferReconciliationEchoHandler(
        InventoryTransferDto transfer)
        : HttpMessageHandler
    {
        public int PullCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/sync/push")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult())
                });
            }

            if (path == "/sync/pull")
            {
                PullCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPullResponse
                    {
                        InventoryTransfers = [transfer],
                        CurrentServerRevision = transfer.Revision
                    })
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class DelayedPullHandler(
        Exception? pullException = null,
        SyncPullResponse? response = null,
        SyncPullResponse? subsequentResponse = null) : HttpMessageHandler
    {
        private readonly TaskCompletionSource<bool> _releasePull =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> PullReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PullCount { get; private set; }

        public void ReleasePull()
            => _releasePull.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path != "/sync/pull")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            PullCount++;
            PullReceived.TrySetResult(true);
            await _releasePull.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            if (pullException is not null)
                throw pullException;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(
                    (PullCount > 1
                        ? subsequentResponse ?? response
                        : response) ??
                    new SyncPullResponse
                    {
                        CurrentServerRevision = 1
                    })
            };
        }
    }

    private sealed class EmptyPushThenPullHandler
        : HttpMessageHandler
    {
        public int PushCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path =
                request.RequestUri?.AbsolutePath ??
                string.Empty;
            if (path == "/sync/push")
            {
                PushCount++;
                return Task.FromResult(
                    new HttpResponseMessage(
                        HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(
                            new SyncPushResult
                            {
                                CurrentServerRevision = 1
                            })
                    });
            }

            if (path == "/sync/pull")
            {
                return Task.FromResult(
                    new HttpResponseMessage(
                        HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(
                            new SyncPullResponse
                            {
                                CurrentServerRevision = 1
                            })
                    });
            }

            return Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.NotFound));
        }
    }

    private sealed class DelayedPushAckThenEmptyPullHandler : HttpMessageHandler
    {
        private readonly Guid _entityId;
        private readonly string _entityName;
        private readonly long _acceptedRevision;
        private readonly DateTime _acceptedUpdatedAtUtc;
        private readonly string? _assignedInvoiceNumber;
        private readonly string? _assignedTaxInvoiceNumber;
        private readonly Exception? _pushException;
        private readonly SyncPullResponse? _pullResponse;
        private readonly Func<CancellationToken, Task>?
            _beforePullResponseAsync;
        private readonly TaskCompletionSource<bool> _releasePush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DelayedPushAckThenEmptyPullHandler(
            Guid entityId,
            string entityName,
            long acceptedRevision,
            DateTime acceptedUpdatedAtUtc,
            string? assignedInvoiceNumber = null,
            string? assignedTaxInvoiceNumber = null,
            Exception? pushException = null,
            SyncPullResponse? pullResponse = null,
            Func<CancellationToken, Task>?
                beforePullResponseAsync = null)
        {
            _entityId = entityId;
            _entityName = entityName;
            _acceptedRevision = acceptedRevision;
            _acceptedUpdatedAtUtc = acceptedUpdatedAtUtc;
            _assignedInvoiceNumber = assignedInvoiceNumber;
            _assignedTaxInvoiceNumber = assignedTaxInvoiceNumber;
            _pushException = pushException;
            _pullResponse = pullResponse;
            _beforePullResponseAsync =
                beforePullResponseAsync;
        }

        public TaskCompletionSource<SyncPushRequest> PushReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PushCount { get; private set; }

        public int PullCount { get; private set; }

        public void ReleasePush()
            => _releasePush.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (path == "/sync/push")
            {
                PushCount++;
                var pushedRequest = await request.Content!
                    .ReadFromJsonAsync<SyncPushRequest>(cancellationToken: cancellationToken);
                Assert.NotNull(pushedRequest);
                PushReceived.TrySetResult(pushedRequest!);
                await _releasePush.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
                if (_pushException is not null)
                    throw _pushException;

                var result = new SyncPushResult
                {
                    AcceptedCount = 1,
                    CurrentServerRevision = _acceptedRevision,
                    AcceptedRevisions =
                    [
                        new SyncAcceptedRevisionDto
                        {
                            EntityName = _entityName,
                            EntityId = _entityId,
                            Revision = _acceptedRevision,
                            UpdatedAtUtc = _acceptedUpdatedAtUtc
                        }
                    ]
                };
                if (!string.IsNullOrWhiteSpace(_assignedInvoiceNumber))
                    result.AssignedInvoiceNumbers[_entityId] = _assignedInvoiceNumber;
                if (!string.IsNullOrWhiteSpace(_assignedTaxInvoiceNumber))
                    result.AssignedTaxInvoiceNumbers[_entityId] = _assignedTaxInvoiceNumber;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(result)
                };
            }

            if (path == "/sync/pull")
            {
                PullCount++;
                if (_beforePullResponseAsync is not null)
                {
                    await _beforePullResponseAsync(
                        cancellationToken);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(
                        _pullResponse ??
                        new SyncPullResponse
                        {
                            CurrentServerRevision = _acceptedRevision
                        })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class DelayedServerNewerConflictThenAckHandler
        : HttpMessageHandler
    {
        private readonly Guid _unitId;
        private readonly long _serverRevision;
        private readonly long _acceptedRevision;
        private readonly DateTime _acceptedUpdatedAtUtc;
        private readonly bool _includeFullServerSnapshot;
        private readonly TaskCompletionSource<bool> _releaseFirstPush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DelayedServerNewerConflictThenAckHandler(
            Guid unitId,
            long serverRevision,
            long acceptedRevision,
            DateTime acceptedUpdatedAtUtc,
            bool includeFullServerSnapshot = true)
        {
            _unitId = unitId;
            _serverRevision = serverRevision;
            _acceptedRevision = acceptedRevision;
            _acceptedUpdatedAtUtc = acceptedUpdatedAtUtc;
            _includeFullServerSnapshot = includeFullServerSnapshot;
        }

        public TaskCompletionSource<SyncPushRequest> FirstPushReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<SyncPushRequest> PushRequests { get; } = [];

        public void ReleaseFirstPush()
            => _releaseFirstPush.TrySetResult(true);

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
                if (PushRequests.Count == 1)
                {
                    FirstPushReceived.TrySetResult(pushedRequest!);
                    await _releaseFirstPush.Task.WaitAsync(
                        TimeSpan.FromSeconds(15),
                        cancellationToken);
                    var pushedUnit = Assert.Single(pushedRequest!.Units);
                    var serverJson = _includeFullServerSnapshot
                        ? JsonSerializer.Serialize(new UnitDto
                        {
                            Id = pushedUnit.Id,
                            Name = pushedUnit.Name,
                            IsActive = pushedUnit.IsActive,
                            IsDeleted = pushedUnit.IsDeleted,
                            CreatedAtUtc = pushedUnit.CreatedAtUtc,
                            UpdatedAtUtc = _acceptedUpdatedAtUtc,
                            Revision = _serverRevision,
                            ExpectedRevision = pushedUnit.ExpectedRevision,
                            MutationId = pushedUnit.MutationId,
                            MutationCreatedAtUtc = pushedUnit.MutationCreatedAtUtc
                        })
                        : JsonSerializer.Serialize(
                            new { Revision = _serverRevision });
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new SyncPushResult
                        {
                            ConflictCount = 1,
                            CurrentServerRevision = _serverRevision,
                            Conflicts =
                            [
                                new ConflictLogDto
                                {
                                    EntityName = "Unit",
                                    EntityId = _unitId.ToString("D"),
                                    Reason = "Server version is newer.",
                                    ClientJson =
                                        JsonSerializer.Serialize(pushedUnit),
                                    ServerJson = serverJson
                                }
                            ]
                        })
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult
                    {
                        AcceptedCount = 1,
                        CurrentServerRevision = _acceptedRevision,
                        AcceptedRevisions =
                        [
                            new SyncAcceptedRevisionDto
                            {
                                EntityName = "Unit",
                                EntityId = _unitId,
                                Revision = _acceptedRevision,
                                UpdatedAtUtc = _acceptedUpdatedAtUtc
                            }
                        ]
                    })
                };
            }

            if (path == "/sync/pull")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPullResponse
                    {
                        CurrentServerRevision = _acceptedRevision
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class DelayedDependencyConflictThenAckHandler
        : HttpMessageHandler
    {
        private readonly Guid _optionId;
        private readonly long _serverRevision;
        private readonly long _acceptedRevision;
        private readonly TaskCompletionSource<bool> _releasePush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DelayedDependencyConflictThenAckHandler(
            Guid optionId,
            long serverRevision,
            long acceptedRevision)
        {
            _optionId = optionId;
            _serverRevision = serverRevision;
            _acceptedRevision = acceptedRevision;
        }

        public TaskCompletionSource<SyncPushRequest> PushReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<SyncPushRequest> PushRequests { get; } = [];

        public void ReleasePush()
            => _releasePush.TrySetResult(true);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != "/sync/push")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var pushedRequest = await request.Content!
                .ReadFromJsonAsync<SyncPushRequest>(
                    cancellationToken: cancellationToken);
            Assert.NotNull(pushedRequest);
            PushRequests.Add(pushedRequest!);
            if (PushRequests.Count == 1)
            {
                PushReceived.TrySetResult(pushedRequest!);
                await _releasePush.Task.WaitAsync(
                    TimeSpan.FromSeconds(15),
                    cancellationToken);
                var pushedOption = Assert.Single(
                    pushedRequest!.PriceGradeOptions);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult
                    {
                        ConflictCount = 1,
                        CurrentServerRevision = _serverRevision,
                        Conflicts =
                        [
                            new ConflictLogDto
                            {
                                EntityName = "PriceGradeOption",
                                EntityId = _optionId.ToString("D"),
                                Reason = "Server version is newer.",
                                ClientJson =
                                    JsonSerializer.Serialize(pushedOption),
                                ServerJson = JsonSerializer.Serialize(
                                    new { Revision = _serverRevision })
                            }
                        ]
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SyncPushResult
                {
                    AcceptedCount = 1,
                    CurrentServerRevision = _acceptedRevision,
                    AcceptedRevisions =
                    [
                        new SyncAcceptedRevisionDto
                        {
                            EntityName = "PriceGradeOption",
                            EntityId = _optionId,
                            Revision = _acceptedRevision,
                            UpdatedAtUtc = DateTime.UtcNow
                        }
                    ]
                })
            };
        }
    }

    private sealed class
        CanonicalizedRentalManagementCompanyDependencyConflictHandler
        : HttpMessageHandler
    {
        private readonly Guid _canonicalServerId;
        private readonly long _serverRevision;
        private readonly string _conflictReason;

        public CanonicalizedRentalManagementCompanyDependencyConflictHandler(
            Guid canonicalServerId,
            long serverRevision,
            string conflictReason =
                "Expected revision mismatch. client=7, server=8")
        {
            _canonicalServerId = canonicalServerId;
            _serverRevision = serverRevision;
            _conflictReason = conflictReason;
        }

        public List<SyncPushRequest> PushRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != "/sync/push")
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var pushedRequest = await request.Content!
                .ReadFromJsonAsync<SyncPushRequest>(
                    cancellationToken: cancellationToken);
            Assert.NotNull(pushedRequest);
            PushRequests.Add(pushedRequest!);
            var pushedCompany = Assert.Single(
                pushedRequest!.RentalManagementCompanies);
            var canonicalClient = JsonSerializer.Deserialize<
                RentalManagementCompanyDto>(
                JsonSerializer.Serialize(pushedCompany))!;
            canonicalClient.Id = _canonicalServerId;
            var canonicalServer = JsonSerializer.Deserialize<
                RentalManagementCompanyDto>(
                JsonSerializer.Serialize(canonicalClient))!;
            canonicalServer.Revision = _serverRevision;
            canonicalServer.ExpectedRevision = _serverRevision;
            canonicalServer.UpdatedAtUtc = DateTime.UtcNow;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new SyncPushResult
                {
                    ConflictCount = 1,
                    CurrentServerRevision = _serverRevision,
                    Conflicts =
                    [
                        new ConflictLogDto
                        {
                            EntityName = "RentalManagementCompany",
                            EntityId = _canonicalServerId.ToString("D"),
                            Reason = _conflictReason,
                            ClientJson =
                                JsonSerializer.Serialize(canonicalClient),
                            ServerJson =
                                JsonSerializer.Serialize(canonicalServer)
                        }
                    ]
                })
            };
        }
    }

    private sealed class InventoryTransferServerNewerConflictHandler
        : HttpMessageHandler
    {
        private readonly Guid _transferId;
        private readonly long _serverRevision;

        public InventoryTransferServerNewerConflictHandler(
            Guid transferId,
            long serverRevision)
        {
            _transferId = transferId;
            _serverRevision = serverRevision;
        }

        public List<SyncPushRequest> PushRequests { get; } = [];

        public TaskCompletionSource<SyncPushRequest> PushReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource<bool> _releasePush =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PullCount { get; private set; }

        public void ReleasePush()
            => _releasePush.TrySetResult(true);

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
                PushReceived.TrySetResult(pushedRequest!);
                await _releasePush.Task.WaitAsync(
                    TimeSpan.FromSeconds(15),
                    cancellationToken);
                var pushedTransfer = Assert.Single(
                    pushedRequest!.InventoryTransfers);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult
                    {
                        ConflictCount = 1,
                        CurrentServerRevision = _serverRevision,
                        Conflicts =
                        [
                            new ConflictLogDto
                            {
                                EntityName = "InventoryTransfer",
                                EntityId = _transferId.ToString("D"),
                                Reason = "Server version is newer.",
                                ClientJson =
                                    JsonSerializer.Serialize(pushedTransfer),
                                ServerJson = JsonSerializer.Serialize(
                                    new { Revision = _serverRevision })
                            }
                        ]
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
                        CurrentServerRevision = _serverRevision
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class InventoryTransferPurgedNoopHandler
        : HttpMessageHandler
    {
        private readonly Guid _transferId;
        private readonly Guid _receiptId;
        private readonly long _purgeRevision;
        private readonly long _currentServerRevision;
        private readonly DateTime _purgedAtUtc;
        private readonly bool _delayPushResponse;
        private readonly bool _includePurgeReceipt;
        private readonly TaskCompletionSource<bool> _releasePushResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InventoryTransferPurgedNoopHandler(
            Guid transferId,
            Guid receiptId,
            long purgeRevision,
            long currentServerRevision,
            DateTime purgedAtUtc,
            bool delayPushResponse,
            bool includePurgeReceipt)
        {
            _transferId = transferId;
            _receiptId = receiptId;
            _purgeRevision = purgeRevision;
            _currentServerRevision = currentServerRevision;
            _purgedAtUtc = purgedAtUtc;
            _delayPushResponse = delayPushResponse;
            _includePurgeReceipt = includePurgeReceipt;
        }

        public List<SyncPushRequest> PushRequests { get; } = [];

        public TaskCompletionSource<SyncPushRequest> PushReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PullCount { get; private set; }

        public string LastPullQuery { get; private set; } = string.Empty;

        public void ReleasePushResponse()
            => _releasePushResponse.TrySetResult(true);

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
                Assert.False(pushedTransfer.IsDeleted);
                PushReceived.TrySetResult(pushedRequest);
                if (_delayPushResponse)
                {
                    await _releasePushResponse.Task.WaitAsync(
                        TimeSpan.FromSeconds(15),
                        cancellationToken);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult
                    {
                        AcceptedCount = 1,
                        CurrentServerRevision = _currentServerRevision,
                        AcceptedRevisions =
                        [
                            new SyncAcceptedRevisionDto
                            {
                                EntityName = "InventoryTransfer",
                                EntityId = _transferId,
                                Revision = _purgeRevision,
                                UpdatedAtUtc = _purgedAtUtc,
                                IsDeleted = true
                            }
                        ],
                        PurgeRecords = _includePurgeReceipt
                            ?
                            [
                                new RecycleBinPurgeRecordDto
                                {
                                    Id = _receiptId,
                                    Kind = "inventory-transfer",
                                    EntityId = _transferId,
                                    TenantCode =
                                        TenantScopeCatalog.UsenetGroup,
                                    OfficeCode =
                                        OfficeCodeCatalog.Shared,
                                    SourceOfficeCode =
                                        OfficeCodeCatalog.Usenet,
                                    TargetOfficeCode =
                                        OfficeCodeCatalog.Yeonsu,
                                    Revision = _purgeRevision,
                                    PurgedAtUtc = _purgedAtUtc,
                                    CreatedAtUtc = _purgedAtUtc,
                                    UpdatedAtUtc = _purgedAtUtc
                                }
                            ]
                            : [],
                        Notices =
                        [
                            new SyncNoticeDto
                            {
                                EntityName = "InventoryTransfer",
                                EntityId = _transferId.ToString("D"),
                                Code =
                                    "inventory-transfer-purged-mutation-noop",
                                Message =
                                    "The inventory transfer was already permanently removed."
                            }
                        ]
                    })
                };
            }

            if (path == "/sync/pull")
            {
                PullCount++;
                LastPullQuery = request.RequestUri?.Query ?? string.Empty;
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

    private sealed class InventoryTransferAcceptedThenEmptyPullHandler
        : HttpMessageHandler
    {
        private readonly Guid _transferId;
        private readonly long _firstAcceptedRevision;
        private readonly long _secondAcceptedRevision;

        public InventoryTransferAcceptedThenEmptyPullHandler(
            Guid transferId,
            long firstAcceptedRevision,
            long secondAcceptedRevision)
        {
            _transferId = transferId;
            _firstAcceptedRevision = firstAcceptedRevision;
            _secondAcceptedRevision = secondAcceptedRevision;
        }

        public List<SyncPushRequest> PushRequests { get; } = [];

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
                Assert.Single(pushedRequest!.InventoryTransfers);
                var acceptedRevision = PushRequests.Count == 1
                    ? _firstAcceptedRevision
                    : _secondAcceptedRevision;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult
                    {
                        AcceptedCount = 1,
                        CurrentServerRevision = acceptedRevision,
                        AcceptedRevisions =
                        [
                            new SyncAcceptedRevisionDto
                            {
                                EntityName = "InventoryTransfer",
                                EntityId = _transferId,
                                Revision = acceptedRevision,
                                UpdatedAtUtc = DateTime.UtcNow
                            }
                        ]
                    })
                };
            }

            if (path == "/sync/pull")
            {
                var currentRevision = PushRequests.Count <= 1
                    ? _firstAcceptedRevision
                    : _secondAcceptedRevision;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPullResponse
                    {
                        CurrentServerRevision = currentRevision
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class
        InventoryTransferStockAtomicityRollbackThenAcceptHandler
        : HttpMessageHandler
    {
        private readonly Guid _transferId;
        private readonly Guid _blockedItemId;
        private readonly Guid _unrelatedItemId;
        private readonly long _acceptedItemRevision;

        public InventoryTransferStockAtomicityRollbackThenAcceptHandler(
            Guid transferId,
            Guid blockedItemId,
            Guid unrelatedItemId,
            long acceptedItemRevision)
        {
            _transferId = transferId;
            _blockedItemId = blockedItemId;
            _unrelatedItemId = unrelatedItemId;
            _acceptedItemRevision = acceptedItemRevision;
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

                if (PushRequests.Count == 1)
                {
                    var pushedTransfer = Assert.Single(
                        pushedRequest!.InventoryTransfers,
                        transfer => transfer.Id == _transferId);
                    Assert.Single(
                        pushedRequest.Items,
                        item => item.Id == _blockedItemId);
                    Assert.Single(
                        pushedRequest.Items,
                        item => item.Id == _unrelatedItemId);
                    Assert.Contains(
                        pushedRequest.ItemWarehouseStocks,
                        stock => stock.ItemId == _blockedItemId);
                    Assert.Contains(
                        pushedRequest.ItemWarehouseStocks,
                        stock => stock.ItemId == _unrelatedItemId);

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new SyncPushResult
                        {
                            AcceptedCount = 0,
                            ConflictCount = 1,
                            DuplicateMutationCount = 0,
                            CurrentServerRevision = 90,
                            AcceptedRevisions = [],
                            AcceptedItemWarehouseStockKeys = [],
                            AssignedInvoiceNumbers = [],
                            AssignedTaxInvoiceNumbers = [],
                            Conflicts =
                            [
                                new ConflictLogDto
                                {
                                    EntityName =
                                        "InventoryTransfer",
                                    EntityId =
                                        _transferId.ToString("D"),
                                    Reason =
                                        "Insufficient source stock.",
                                    ClientJson =
                                        JsonSerializer.Serialize(
                                            pushedTransfer),
                                    ServerJson = JsonSerializer.Serialize(
                                        new { Revision = 7 })
                                }
                            ],
                            Notices =
                            [
                                new SyncNoticeDto
                                {
                                    EntityName =
                                        "InventoryTransfer",
                                    EntityId = string.Empty,
                                    Code =
                                        "inventory-transfer-stock-atomicity-rollback",
                                    Message =
                                        "Inventory transfer stock atomicity rollback."
                                }
                            ]
                        })
                    };
                }

                Assert.Empty(pushedRequest!.InventoryTransfers);
                Assert.DoesNotContain(
                    pushedRequest.Items,
                    item => item.Id == _blockedItemId);
                Assert.DoesNotContain(
                    pushedRequest.ItemWarehouseStocks,
                    stock => stock.ItemId == _blockedItemId);
                var acceptedItem = Assert.Single(
                    pushedRequest.Items,
                    item => item.Id == _unrelatedItemId);
                var acceptedStock = Assert.Single(
                    pushedRequest.ItemWarehouseStocks,
                    stock => stock.ItemId == _unrelatedItemId);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult
                    {
                        AcceptedCount = 1,
                        CurrentServerRevision = 91,
                        AcceptedRevisions =
                        [
                            new SyncAcceptedRevisionDto
                            {
                                EntityName = "Item",
                                EntityId = acceptedItem.Id,
                                Revision = _acceptedItemRevision,
                                UpdatedAtUtc =
                                    DateTime.UtcNow.AddMinutes(1)
                            }
                        ],
                        AcceptedItemWarehouseStockKeys =
                        [
                            new SyncAcceptedItemWarehouseStockKeyDto
                            {
                                ItemId = acceptedStock.ItemId,
                                WarehouseCode =
                                    acceptedStock.WarehouseCode
                            }
                        ]
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
                        CurrentServerRevision =
                            PushRequests.Count == 1 ? 90 : 91
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class
        InventoryTransferStockAtomicityRollbackThenAcceptEditedHandler
        : HttpMessageHandler
    {
        private readonly Guid _transferId;
        private readonly Guid _itemId;
        private readonly long _acceptedTransferRevision;
        private readonly long _acceptedItemRevision;

        public InventoryTransferStockAtomicityRollbackThenAcceptEditedHandler(
            Guid transferId,
            Guid itemId,
            long acceptedTransferRevision,
            long acceptedItemRevision)
        {
            _transferId = transferId;
            _itemId = itemId;
            _acceptedTransferRevision = acceptedTransferRevision;
            _acceptedItemRevision = acceptedItemRevision;
        }

        public List<SyncPushRequest> PushRequests { get; } = [];

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
                    pushedRequest!.InventoryTransfers,
                    transfer => transfer.Id == _transferId);

                if (PushRequests.Count == 1)
                {
                    Assert.Contains(
                        pushedRequest.Items,
                        item => item.Id == _itemId);
                    Assert.Contains(
                        pushedRequest.ItemWarehouseStocks,
                        stock => stock.ItemId == _itemId);
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new SyncPushResult
                        {
                            AcceptedCount = 0,
                            ConflictCount = 1,
                            DuplicateMutationCount = 0,
                            CurrentServerRevision = 90,
                            AcceptedRevisions = [],
                            AcceptedItemWarehouseStockKeys = [],
                            AssignedInvoiceNumbers = [],
                            AssignedTaxInvoiceNumbers = [],
                            Conflicts =
                            [
                                new ConflictLogDto
                                {
                                    EntityName = "InventoryTransfer",
                                    EntityId = _transferId.ToString("D"),
                                    Reason = "Insufficient source stock.",
                                    ClientJson =
                                        JsonSerializer.Serialize(
                                            pushedTransfer),
                                    ServerJson = JsonSerializer.Serialize(
                                        new { Revision = 7 })
                                }
                            ],
                            Notices =
                            [
                                new SyncNoticeDto
                                {
                                    EntityName = "InventoryTransfer",
                                    EntityId = string.Empty,
                                    Code =
                                        "inventory-transfer-stock-atomicity-rollback",
                                    Message =
                                        "Inventory transfer stock atomicity rollback."
                                }
                            ]
                        })
                    };
                }

                Assert.Equal(2, PushRequests.Count);
                var pushedItem = Assert.Single(
                    pushedRequest.Items,
                    item => item.Id == _itemId);
                var acceptedAtUtc = DateTime.UtcNow.AddMinutes(1);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult
                    {
                        AcceptedCount = 2,
                        ConflictCount = 0,
                        DuplicateMutationCount = 0,
                        CurrentServerRevision = 92,
                        AcceptedRevisions =
                        [
                            new SyncAcceptedRevisionDto
                            {
                                EntityName = "InventoryTransfer",
                                EntityId = pushedTransfer.Id,
                                Revision = _acceptedTransferRevision,
                                UpdatedAtUtc = acceptedAtUtc
                            },
                            new SyncAcceptedRevisionDto
                            {
                                EntityName = "Item",
                                EntityId = pushedItem.Id,
                                Revision = _acceptedItemRevision,
                                UpdatedAtUtc = acceptedAtUtc
                            }
                        ],
                        AcceptedItemWarehouseStockKeys =
                            pushedRequest.ItemWarehouseStocks
                                .Where(stock => stock.ItemId == _itemId)
                                .Select(stock =>
                                    new SyncAcceptedItemWarehouseStockKeyDto
                                    {
                                        ItemId = stock.ItemId,
                                        WarehouseCode =
                                            stock.WarehouseCode
                                    })
                                .ToList()
                    })
                };
            }

            if (path == "/sync/pull")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPullResponse
                    {
                        CurrentServerRevision =
                            PushRequests.Count == 1 ? 90 : 92
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private sealed class
        InventoryTransferMalformedAtomicityRollbackNoticeHandler
        : HttpMessageHandler
    {
        private readonly Guid _transferId;
        private readonly string _noticeEntityName;
        private readonly bool _useNonEmptyEntityId;
        private readonly bool _includeExtraNotice;
        private readonly string? _extraConflictEntityName;
        private readonly Guid _extraConflictEntityId = Guid.NewGuid();

        public InventoryTransferMalformedAtomicityRollbackNoticeHandler(
            Guid transferId,
            string noticeEntityName,
            bool useNonEmptyEntityId,
            bool includeExtraNotice,
            string? extraConflictEntityName)
        {
            _transferId = transferId;
            _noticeEntityName = noticeEntityName;
            _useNonEmptyEntityId = useNonEmptyEntityId;
            _includeExtraNotice = includeExtraNotice;
            _extraConflictEntityName = extraConflictEntityName;
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
                    pushedRequest!.InventoryTransfers,
                    transfer => transfer.Id == _transferId);
                Assert.Empty(pushedRequest.ItemWarehouseStocks);

                var notices = new List<SyncNoticeDto>
                {
                    new()
                    {
                        EntityName = _noticeEntityName,
                        EntityId = _useNonEmptyEntityId
                            ? _transferId.ToString("D")
                            : string.Empty,
                        Code =
                            "inventory-transfer-stock-atomicity-rollback",
                        Message =
                            "Inventory transfer stock atomicity rollback."
                    }
                };
                if (_includeExtraNotice)
                {
                    notices.Add(new SyncNoticeDto
                    {
                        EntityName = "InventoryTransfer",
                        EntityId = string.Empty,
                        Code = "additional-warning",
                        Message = "Additional warning."
                    });
                }

                var conflicts = new List<ConflictLogDto>
                {
                    new()
                    {
                        EntityName = "InventoryTransfer",
                        EntityId = _transferId.ToString("D"),
                        Reason = "Insufficient source stock.",
                        ClientJson =
                            JsonSerializer.Serialize(
                                pushedTransfer),
                        ServerJson = JsonSerializer.Serialize(
                            new { Revision = 7 })
                    }
                };
                if (!string.IsNullOrWhiteSpace(
                        _extraConflictEntityName))
                {
                    conflicts.Add(new ConflictLogDto
                    {
                        EntityName = _extraConflictEntityName,
                        EntityId =
                            _extraConflictEntityId.ToString("D"),
                        Reason = "Unrelated conflict.",
                        ClientJson = "{}",
                        ServerJson = "{}"
                    });
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new SyncPushResult
                    {
                        AcceptedCount = 0,
                        ConflictCount = conflicts.Count,
                        DuplicateMutationCount = 0,
                        CurrentServerRevision = 90,
                        AcceptedRevisions = [],
                        AcceptedItemWarehouseStockKeys = [],
                        AssignedInvoiceNumbers = [],
                        AssignedTaxInvoiceNumbers = [],
                        Conflicts = conflicts,
                        Notices = notices
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
                        CurrentServerRevision = 90
                    })
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
