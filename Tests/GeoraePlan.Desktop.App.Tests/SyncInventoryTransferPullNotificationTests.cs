using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncInventoryTransferPullNotificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransferOnlyPull_NewerOrTombstone_PublishesOnceAfterCommit(
        bool isTombstone)
    {
        await ExecuteWithSyncAsync(
            $"georaeplan-transfer-pull-notification-{isTombstone}",
            async (db, sync, notifier, _, _) =>
            {
                var now = new DateTime(
                    2026,
                    8,
                    1,
                    3,
                    0,
                    0,
                    DateTimeKind.Utc);
                var transferId = Guid.NewGuid();
                await SeedTransferAsync(db, transferId, now, revision: 10);

                var eventCount = 0;
                notifier.InventoryStateChanged += (_, _) => eventCount++;
                sync.AfterPulledPurgeRecordsAsyncForTesting = _ =>
                {
                    Assert.Equal(0, eventCount);
                    return Task.CompletedTask;
                };

                await InvokeApplyPullAsync(
                    sync,
                    CreateTransferOnlyPull(
                        transferId,
                        now.AddMinutes(1),
                        revision: 11,
                        isTombstone));

                Assert.Equal(1, eventCount);
                db.ChangeTracker.Clear();
                var stored = await db.InventoryTransfers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(transfer => transfer.Id == transferId);
                Assert.Equal(isTombstone, stored.IsDeleted);
                Assert.Equal(11, stored.Revision);
                Assert.Equal("server-newer", stored.Memo);
            });
    }

    [Fact]
    public async Task TransferOnlyPull_OlderServerRow_DoesNotPublish()
    {
        await ExecuteWithSyncAsync(
            "georaeplan-transfer-pull-notification-noop",
            async (db, sync, notifier, _, _) =>
            {
                var now = new DateTime(
                    2026,
                    8,
                    1,
                    3,
                    10,
                    0,
                    DateTimeKind.Utc);
                var transferId = Guid.NewGuid();
                await SeedTransferAsync(db, transferId, now, revision: 10);

                var eventCount = 0;
                notifier.InventoryStateChanged += (_, _) => eventCount++;

                await InvokeApplyPullAsync(
                    sync,
                    CreateTransferOnlyPull(
                        transferId,
                        now.AddMinutes(-1),
                        revision: 9,
                        isTombstone: false));

                Assert.Equal(0, eventCount);
                db.ChangeTracker.Clear();
                var stored = await db.InventoryTransfers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(transfer => transfer.Id == transferId);
                Assert.Equal(10, stored.Revision);
                Assert.Equal("local-existing", stored.Memo);
            });
    }

    [Fact]
    public async Task TransferOnlyPull_Rollback_DoesNotPublishOrPersistChange()
    {
        await ExecuteWithSyncAsync(
            "georaeplan-transfer-pull-notification-rollback",
            async (db, sync, notifier, _, _) =>
            {
                var now = new DateTime(
                    2026,
                    8,
                    1,
                    3,
                    20,
                    0,
                    DateTimeKind.Utc);
                var transferId = Guid.NewGuid();
                await SeedTransferAsync(db, transferId, now, revision: 10);

                var eventCount = 0;
                notifier.InventoryStateChanged += (_, _) => eventCount++;
                sync.AfterPulledPurgeRecordsAsyncForTesting = _ =>
                    throw new InvalidOperationException(
                        "simulated failure before pull commit");

                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => InvokeApplyPullAsync(
                        sync,
                        CreateTransferOnlyPull(
                            transferId,
                            now.AddMinutes(1),
                            revision: 11,
                            isTombstone: false)));

                Assert.Equal(0, eventCount);
                db.ChangeTracker.Clear();
                var stored = await db.InventoryTransfers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(transfer => transfer.Id == transferId);
                Assert.Equal(10, stored.Revision);
                Assert.Equal("local-existing", stored.Memo);
            });
    }

    [Fact]
    public async Task TransferOnlyPull_DirtyActiveLocalAndNewerRemoteTombstone_PreservesDraftAndConverges()
    {
        await ExecuteWithSyncAsync(
            "georaeplan-transfer-pull-dirty-tombstone",
            async (db, sync, notifier, local, session) =>
            {
                var now = new DateTime(
                    2026,
                    8,
                    1,
                    3,
                    25,
                    0,
                    DateTimeKind.Utc);
                var transferId = Guid.NewGuid();
                await SeedTransferAsync(
                    db,
                    transferId,
                    now,
                    revision: 10,
                    isDirty: true);
                db.SyncOutboxEntries.Add(
                    new LocalSyncOutboxEntry
                    {
                        MutationId = Guid.NewGuid().ToString("N"),
                        DeviceId = "dirty-tombstone-device",
                        EntityName = nameof(LocalInventoryTransfer),
                        EntityId = transferId,
                        ExpectedRevision = 10,
                        BusinessDatabaseName =
                            TenantScopeCatalog.GetDatabaseName(
                                session.SelectedBusinessDatabaseName),
                        TenantCode = session.TenantCode,
                        OfficeCode = session.OfficeCode,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                        SessionId = session.SessionId,
                        UserId = session.User!.UserId,
                        Status = "Prepared"
                    });
                await db.SaveChangesAsync();
                await local.SetSettingAsync("LastSyncRevision", "10");
                db.ChangeTracker.Clear();

                var eventCount = 0;
                notifier.InventoryStateChanged += (_, _) => eventCount++;

                await InvokeApplyPullAsync(
                    sync,
                    CreateTransferOnlyPull(
                        transferId,
                        now.AddMinutes(1),
                        revision: 11,
                        isTombstone: true),
                    updateSyncRevision: true);

                Assert.Equal(1, eventCount);

                db.ChangeTracker.Clear();
                var stored = await db.InventoryTransfers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(transfer => transfer.Id == transferId);
                Assert.True(stored.IsDeleted);
                Assert.False(stored.IsDirty);
                Assert.Equal(11, stored.Revision);
                Assert.Equal("server-newer", stored.Memo);
                Assert.Equal(
                    "11",
                    await local.GetSettingAsync("LastSyncRevision"));

                var outbox = await db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(entry =>
                        entry.EntityName == nameof(LocalInventoryTransfer) &&
                        entry.EntityId == transferId);
                Assert.Equal("Failed", outbox.Status);
                Assert.StartsWith(
                    InventoryTransferTombstoneConflictPolicy
                        .OutboxErrorPrefix,
                    outbox.ErrorMessage,
                    StringComparison.Ordinal);

                var conflict = await db
                    .InventoryTransferTombstoneConflicts
                    .AsNoTracking()
                    .SingleAsync(current =>
                        current.TransferId == transferId);
                Assert.Equal(
                    InventoryTransferTombstoneConflictPolicy
                        .UnresolvedStatus,
                    conflict.Status);
                Assert.Equal(10, conflict.LocalRevision);
                Assert.Equal(11, conflict.ServerRevision);
                Assert.Contains(
                    "local-existing",
                    conflict.LocalSnapshotJson,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "server-newer",
                    conflict.ServerTombstoneJson,
                    StringComparison.Ordinal);
                Assert.Contains(
                    outbox.MutationId,
                    conflict.OutboxMutationIdsJson,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task TransferTombstoneConflict_ExactRouteScope_CapturesOnlyExistingMutationAndAcknowledgesOnlyUserResolvedCapture()
    {
        await ExecuteWithSyncAsync(
            "georaeplan-transfer-tombstone-exact-route-outbox",
            async (db, sync, _, local, session) =>
            {
                var now = new DateTime(2026, 8, 1, 3, 26, 0, DateTimeKind.Utc);
                var transferId = Guid.NewGuid();
                var capturedOutboxId = Guid.NewGuid();
                var capturedMutationId = Guid.NewGuid().ToString("N");
                var foreignBusinessOutboxId = Guid.NewGuid();
                await SeedTransferAsync(db, transferId, now, revision: 10, isDirty: true);

                var businessDatabaseName = TenantScopeCatalog.GetDatabaseName(
                    session.SelectedBusinessDatabaseName);
                db.SyncOutboxEntries.AddRange(
                    CreateTransferOutbox(
                        capturedOutboxId,
                        capturedMutationId,
                        transferId,
                        businessDatabaseName,
                        TenantScopeCatalog.UsenetGroup,
                        OfficeCodeCatalog.Usenet,
                        OfficeCodeCatalog.Yeonsu,
                        session,
                        now.AddSeconds(1)),
                    CreateTransferOutbox(
                        foreignBusinessOutboxId,
                        Guid.NewGuid().ToString("N"),
                        transferId,
                        TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld),
                        TenantScopeCatalog.Itworld,
                        OfficeCodeCatalog.Itworld,
                        OfficeCodeCatalog.Itworld,
                        session,
                        now.AddSeconds(2)));
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                await InvokeApplyPullAsync(
                    sync,
                    CreateTransferOnlyPull(
                        transferId,
                        now.AddMinutes(1),
                        revision: 11,
                        isTombstone: true));

                db.ChangeTracker.Clear();
                var conflict = await db.InventoryTransferTombstoneConflicts
                    .AsNoTracking()
                    .SingleAsync(current =>
                        current.TransferId == transferId &&
                        current.BusinessDatabaseName == businessDatabaseName);
                var capturedMutationIds = JsonSerializer.Deserialize<string[]>(
                    conflict.OutboxMutationIdsJson) ?? [];
                var capturedOutbox = await db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(entry => entry.Id == capturedOutboxId);
                var foreignBusinessOutbox = await db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(entry => entry.Id == foreignBusinessOutboxId);

                Assert.Equal(TenantScopeCatalog.UsenetGroup, conflict.TenantCode);
                Assert.Equal(OfficeCodeCatalog.Usenet, conflict.SourceOfficeCode);
                Assert.Equal(OfficeCodeCatalog.Yeonsu, conflict.TargetOfficeCode);
                Assert.Equal([capturedMutationId], capturedMutationIds);
                Assert.Equal("Failed", capturedOutbox.Status);
                Assert.StartsWith(
                    InventoryTransferTombstoneConflictPolicy.OutboxErrorPrefix,
                    capturedOutbox.ErrorMessage,
                    StringComparison.Ordinal);
                Assert.Equal("Prepared", foreignBusinessOutbox.Status);

                await InvokeApplyPullAsync(
                    sync,
                    CreateTransferOnlyPull(
                        transferId,
                        now.AddMinutes(2),
                        revision: 12,
                        isTombstone: true));
                db.ChangeTracker.Clear();
                conflict = await db.InventoryTransferTombstoneConflicts
                    .AsNoTracking()
                    .SingleAsync(current =>
                        current.TransferId == transferId &&
                        current.BusinessDatabaseName ==
                        businessDatabaseName);
                capturedOutbox = await db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(entry => entry.Id == capturedOutboxId);
                Assert.Equal(12, conflict.ServerRevision);
                Assert.Equal(string.Empty, conflict.Resolution);
                Assert.Equal("Failed", capturedOutbox.Status);

                var newerOutboxId = Guid.NewGuid();
                db.SyncOutboxEntries.Add(
                    CreateTransferOutbox(
                        newerOutboxId,
                        Guid.NewGuid().ToString("N"),
                        transferId,
                        businessDatabaseName,
                        TenantScopeCatalog.UsenetGroup,
                        OfficeCodeCatalog.Usenet,
                        OfficeCodeCatalog.Yeonsu,
                        session,
                        now.AddMinutes(3)));
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                Assert.True(
                    await local.ResolveInventoryTransferTombstoneConflictAsync(
                        transferId,
                        InventoryTransferTombstoneConflictPolicy.DiscardedResolution,
                        session));

                db.ChangeTracker.Clear();
                capturedOutbox = await db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(entry => entry.Id == capturedOutboxId);
                var newerOutbox = await db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(entry => entry.Id == newerOutboxId);
                foreignBusinessOutbox = await db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(entry => entry.Id == foreignBusinessOutboxId);

                Assert.Equal("Acknowledged", capturedOutbox.Status);
                Assert.NotNull(capturedOutbox.AcknowledgedAtUtc);
                Assert.Equal(12, capturedOutbox.AcceptedRevision);
                Assert.Equal("Prepared", newerOutbox.Status);
                Assert.Null(newerOutbox.AcknowledgedAtUtc);
                Assert.Equal("Prepared", foreignBusinessOutbox.Status);
                Assert.Null(foreignBusinessOutbox.AcknowledgedAtUtc);
            });
    }

    [Fact]
    public async Task TransferTombstoneConflict_LocalDirtyRouteWinsStaleServerRouteForScopeAndPermissions()
    {
        await ExecuteWithSyncAsync(
            "georaeplan-transfer-tombstone-local-route-scope",
            async (db, sync, _, _, session) =>
            {
                var now = new DateTime(
                    2026,
                    8,
                    1,
                    3,
                    27,
                    30,
                    DateTimeKind.Utc);
                var transferId = Guid.NewGuid();
                await SeedTransferAsync(
                    db,
                    transferId,
                    now,
                    revision: 10,
                    isDirty: true);
                var dirtyLocal = await db.InventoryTransfers
                    .IgnoreQueryFilters()
                    .SingleAsync(
                        transfer => transfer.Id == transferId);
                dirtyLocal.FromWarehouseCode =
                    OfficeCodeCatalog.YeonsuMainWarehouse;
                dirtyLocal.ToWarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse;
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                var businessDatabaseName =
                    TenantScopeCatalog.GetDatabaseName(
                        session.SelectedBusinessDatabaseName);
                var localRouteOutboxId = Guid.NewGuid();
                var staleServerRouteOutboxId = Guid.NewGuid();
                db.SyncOutboxEntries.AddRange(
                    CreateTransferOutbox(
                        localRouteOutboxId,
                        Guid.NewGuid().ToString("N"),
                        transferId,
                        businessDatabaseName,
                        TenantScopeCatalog.UsenetGroup,
                        OfficeCodeCatalog.Yeonsu,
                        OfficeCodeCatalog.Usenet,
                        session,
                        now.AddSeconds(1)),
                    CreateTransferOutbox(
                        staleServerRouteOutboxId,
                        Guid.NewGuid().ToString("N"),
                        transferId,
                        businessDatabaseName,
                        TenantScopeCatalog.UsenetGroup,
                        OfficeCodeCatalog.Usenet,
                        OfficeCodeCatalog.Yeonsu,
                        session,
                        now.AddSeconds(2)));
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                await InvokeApplyPullAsync(
                    sync,
                    CreateTransferOnlyPull(
                        transferId,
                        now.AddMinutes(1),
                        revision: 11,
                        isTombstone: true));

                db.ChangeTracker.Clear();
                var conflict = await db
                    .InventoryTransferTombstoneConflicts
                    .AsNoTracking()
                    .SingleAsync(current =>
                        current.TransferId == transferId &&
                        current.BusinessDatabaseName ==
                        businessDatabaseName);
                var localRouteOutbox = await db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(
                        entry => entry.Id == localRouteOutboxId);
                var staleServerRouteOutbox =
                    await db.SyncOutboxEntries
                        .AsNoTracking()
                        .SingleAsync(
                            entry =>
                                entry.Id ==
                                staleServerRouteOutboxId);

                Assert.Equal(
                    OfficeCodeCatalog.Yeonsu,
                    conflict.SourceOfficeCode);
                Assert.Equal(
                    OfficeCodeCatalog.Usenet,
                    conflict.TargetOfficeCode);
                Assert.Contains(
                    OfficeCodeCatalog.Yeonsu,
                    conflict.LocalSnapshotJson,
                    StringComparison.Ordinal);
                Assert.Equal("Failed", localRouteOutbox.Status);
                Assert.Equal(
                    "Prepared",
                    staleServerRouteOutbox.Status);
                Assert.Contains(
                    localRouteOutbox.MutationId,
                    conflict.OutboxMutationIdsJson,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    staleServerRouteOutbox.MutationId,
                    conflict.OutboxMutationIdsJson,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task TransferTombstoneConflict_NewerActivePull_PreservesUnresolvedShadowAndActionableCapturedOutbox()
    {
        await ExecuteWithSyncAsync(
            "georaeplan-transfer-tombstone-active-restore-shadow",
            async (db, sync, _, _, session) =>
            {
                var now = new DateTime(2026, 8, 1, 3, 28, 0, DateTimeKind.Utc);
                var transferId = Guid.NewGuid();
                var outboxId = Guid.NewGuid();
                var mutationId = Guid.NewGuid().ToString("N");
                await SeedTransferAsync(db, transferId, now, revision: 10, isDirty: true);
                db.SyncOutboxEntries.Add(
                    CreateTransferOutbox(
                        outboxId,
                        mutationId,
                        transferId,
                        TenantScopeCatalog.GetDatabaseName(
                            session.SelectedBusinessDatabaseName),
                        TenantScopeCatalog.UsenetGroup,
                        OfficeCodeCatalog.Usenet,
                        string.Empty,
                        session,
                        now.AddSeconds(1)));
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                await InvokeApplyPullAsync(
                    sync,
                    CreateTransferOnlyPull(
                        transferId,
                        now.AddMinutes(1),
                        revision: 11,
                        isTombstone: true));

                db.ChangeTracker.Clear();
                var capturedOutbox = await db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(entry => entry.Id == outboxId);
                Assert.Equal("Failed", capturedOutbox.Status);

                await InvokeApplyPullAsync(
                    sync,
                    CreateTransferOnlyPull(
                        transferId,
                        now.AddMinutes(2),
                        revision: 12,
                        isTombstone: false));

                db.ChangeTracker.Clear();
                var activeMain = await db.InventoryTransfers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(transfer => transfer.Id == transferId);
                var conflict = await db.InventoryTransferTombstoneConflicts
                    .AsNoTracking()
                    .SingleAsync(current => current.TransferId == transferId);
                capturedOutbox = await db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(entry => entry.Id == outboxId);

                Assert.False(activeMain.IsDeleted);
                Assert.False(activeMain.IsDirty);
                Assert.Equal(12, activeMain.Revision);
                Assert.Equal("server-newer", activeMain.Memo);
                Assert.Equal(
                    InventoryTransferTombstoneConflictPolicy.UnresolvedStatus,
                    conflict.Status);
                Assert.Contains(
                    "local-existing",
                    conflict.LocalSnapshotJson,
                    StringComparison.Ordinal);
                Assert.Contains(
                    mutationId,
                    conflict.OutboxMutationIdsJson,
                    StringComparison.Ordinal);
                Assert.Null(conflict.ResolvedAtUtc);
                Assert.Equal(
                    InventoryTransferTombstoneConflictPolicy
                        .ServerRestoredPendingDecisionResolution,
                    conflict.Resolution);
                Assert.Equal("Failed", capturedOutbox.Status);
                Assert.Null(capturedOutbox.AcknowledgedAtUtc);
                Assert.StartsWith(
                    InventoryTransferTombstoneConflictPolicy.OutboxErrorPrefix,
                    capturedOutbox.ErrorMessage,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task TransferOnlyPull_DirtyRemoteTombstoneFailure_RollsBackDraftShadowOutboxCursorAndEvent()
    {
        await ExecuteWithSyncAsync(
            "georaeplan-transfer-pull-dirty-tombstone-rollback",
            async (db, sync, notifier, local, session) =>
            {
                var now = new DateTime(
                    2026,
                    8,
                    1,
                    3,
                    27,
                    0,
                    DateTimeKind.Utc);
                var transferId = Guid.NewGuid();
                await SeedTransferAsync(
                    db,
                    transferId,
                    now,
                    revision: 10,
                    isDirty: true);
                db.SyncOutboxEntries.Add(
                    new LocalSyncOutboxEntry
                    {
                        MutationId = Guid.NewGuid().ToString("N"),
                        DeviceId = "dirty-tombstone-rollback-device",
                        EntityName = nameof(LocalInventoryTransfer),
                        EntityId = transferId,
                        ExpectedRevision = 10,
                        BusinessDatabaseName =
                            TenantScopeCatalog.GetDatabaseName(
                                session.SelectedBusinessDatabaseName),
                        TenantCode = session.TenantCode,
                        OfficeCode = session.OfficeCode,
                        ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                        SessionId = session.SessionId,
                        UserId = session.User!.UserId,
                        Status = "Prepared"
                    });
                await db.SaveChangesAsync();
                await local.SetSettingAsync("LastSyncRevision", "10");
                db.ChangeTracker.Clear();

                var eventCount = 0;
                notifier.InventoryStateChanged += (_, _) => eventCount++;
                sync.AfterPulledPurgeRecordsAsyncForTesting = _ =>
                    throw new InvalidOperationException(
                        "simulated failure after tombstone conflict capture");

                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => InvokeApplyPullAsync(
                        sync,
                        CreateTransferOnlyPull(
                            transferId,
                            now.AddMinutes(1),
                            revision: 11,
                            isTombstone: true),
                        updateSyncRevision: true));

                Assert.Equal(0, eventCount);
                db.ChangeTracker.Clear();
                var stored = await db.InventoryTransfers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(transfer => transfer.Id == transferId);
                Assert.False(stored.IsDeleted);
                Assert.True(stored.IsDirty);
                Assert.Equal(10, stored.Revision);
                Assert.Equal("local-existing", stored.Memo);
                Assert.Equal(
                    "10",
                    await local.GetSettingAsync("LastSyncRevision"));
                Assert.False(
                    await db.InventoryTransferTombstoneConflicts
                        .AsNoTracking()
                        .AnyAsync(current =>
                            current.TransferId == transferId));

                var outbox = await db.SyncOutboxEntries
                    .AsNoTracking()
                    .SingleAsync(entry =>
                        entry.EntityName == nameof(LocalInventoryTransfer) &&
                        entry.EntityId == transferId);
                Assert.Equal("Prepared", outbox.Status);
                Assert.Equal(string.Empty, outbox.ErrorMessage);
            });
    }

    [Fact]
    public async Task SaveInventoryTransfer_RemoteTombstone_CannotBeResurrected()
    {
        await ExecuteWithSyncAsync(
            "georaeplan-transfer-save-tombstone",
            async (db, _, _, local, session) =>
            {
                var now = new DateTime(
                    2026,
                    8,
                    1,
                    3,
                    30,
                    0,
                    DateTimeKind.Utc);
                var transferId = Guid.NewGuid();
                await SeedTransferAsync(
                    db,
                    transferId,
                    now,
                    revision: 10,
                    isDeleted: true);

                var result = await local.SaveInventoryTransferAsync(
                    new LocalInventoryTransfer
                    {
                        Id = transferId,
                        TransferNumber = "TR-NOTIFICATION",
                        TransferDate = new DateOnly(2026, 8, 1),
                        FromWarehouseCode =
                            DomainConstants.WarehouseUsenetMain,
                        ToWarehouseCode =
                            DomainConstants.WarehouseYeonsuMain,
                        Memo = "attempted-resurrection",
                        CreatedAtUtc = now.AddMinutes(-1),
                        UpdatedAtUtc = now,
                        Revision = 10,
                        Lines =
                        {
                            new LocalInventoryTransferLine
                            {
                                Id = Guid.NewGuid(),
                                TransferId = transferId,
                                ItemId = Guid.NewGuid(),
                                ItemNameOriginal = "tombstone guard item",
                                Unit = "EA",
                                Quantity = 1m
                            }
                        }
                    },
                    session);

                Assert.False(result.Success);
                Assert.True(result.ConcurrencyConflict);
                Assert.Contains("삭제된", result.Message, StringComparison.Ordinal);
                Assert.Contains("다시 불러온", result.Message, StringComparison.Ordinal);

                db.ChangeTracker.Clear();
                var stored = await db.InventoryTransfers
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .SingleAsync(transfer => transfer.Id == transferId);
                Assert.True(stored.IsDeleted);
                Assert.False(stored.IsDirty);
                Assert.Equal(10, stored.Revision);
                Assert.Equal("local-existing", stored.Memo);
            });
    }

    private static async Task ExecuteWithSyncAsync(
        string appRootPrefix,
        Func<
            LocalDbContext,
            SyncService,
            DesktopDataChangeNotifier,
            LocalStateService,
            SessionState,
            Task> assertion)
    {
        var appRoot = Path.Combine(
            Path.GetTempPath(),
            $"{appRootPrefix}-{Guid.NewGuid():N}");
        var previousAppRoot =
            Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        Directory.CreateDirectory(appRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", appRoot);

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var session = CreateAdminSession();
            var dispatcher = new SyncRequestDispatcher();
            var notifier = new DesktopDataChangeNotifier();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                dispatcher,
                session,
                notifier);
            var rental = new RentalStateService(db, local);
            var diagnostics = new SyncDiagnosticsService(session);
            var api = new ErpApiClient(
                new HttpClient
                {
                    BaseAddress = new Uri("http://localhost/")
                },
                session);
            using var sync = new SyncService(
                db,
                local,
                rental,
                api,
                session,
                dispatcher,
                diagnostics);

            await assertion(db, sync, notifier, local, session);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                previousAppRoot);
            SqliteConnection.ClearAllPools();

            var fullTempRoot = Path.GetFullPath(Path.GetTempPath());
            var fullAppRoot = Path.GetFullPath(appRoot);
            if (fullAppRoot.StartsWith(
                    fullTempRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Directory.Exists(fullAppRoot))
                        Directory.Delete(fullAppRoot, recursive: true);
                }
                catch
                {
                    // Cleanup failures must not hide the assertion result.
                }
            }
        }
    }

    private static LocalSyncOutboxEntry CreateTransferOutbox(
        Guid id,
        string mutationId,
        Guid transferId,
        string businessDatabaseName,
        string tenantCode,
        string officeCode,
        string responsibleOfficeCode,
        SessionState session,
        DateTime preparedAtUtc)
        => new()
        {
            Id = id,
            MutationId = mutationId,
            DeviceId = $"transfer-tombstone-{id:N}",
            EntityName = nameof(LocalInventoryTransfer),
            EntityId = transferId,
            ExpectedRevision = 10,
            BusinessDatabaseName = businessDatabaseName,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ResponsibleOfficeCode = responsibleOfficeCode,
            SessionId = session.SessionId,
            UserId = session.User!.UserId,
            Status = "Prepared",
            PreparedAtUtc = preparedAtUtc
        };

    private static async Task SeedTransferAsync(
        LocalDbContext db,
        Guid transferId,
        DateTime updatedAtUtc,
        long revision,
        bool isDeleted = false,
        bool isDirty = false)
    {
        db.InventoryTransfers.Add(
            new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "TR-NOTIFICATION",
                TransferDate = new DateOnly(2026, 8, 1),
                FromWarehouseCode = DomainConstants.WarehouseUsenetMain,
                ToWarehouseCode = DomainConstants.WarehouseYeonsuMain,
                Memo = "local-existing",
                CreatedByUsername = "sync-test",
                LastSavedByUsername = "sync-test",
                LastSavedAtUtc = updatedAtUtc,
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                CreatedAtUtc = updatedAtUtc.AddMinutes(-1),
                UpdatedAtUtc = updatedAtUtc,
                Revision = revision,
                IsDeleted = isDeleted,
                IsDirty = isDirty
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private static SyncPullResponse CreateTransferOnlyPull(
        Guid transferId,
        DateTime updatedAtUtc,
        long revision,
        bool isTombstone)
        => new()
        {
            CurrentServerRevision = revision,
            InventoryTransfers =
            {
                new InventoryTransferDto
                {
                    Id = transferId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    TransferNumber = "TR-NOTIFICATION",
                    TransferDate = new DateOnly(2026, 8, 1),
                    FromWarehouseCode = DomainConstants.WarehouseUsenetMain,
                    ToWarehouseCode = DomainConstants.WarehouseYeonsuMain,
                    Memo = "server-newer",
                    CreatedByUsername = "sync-test",
                    LastSavedByUsername = "sync-test",
                    LastSavedAtUtc = updatedAtUtc,
                    TransferStatus = InventoryTransferStatusNormalizer.Pending,
                    CreatedAtUtc = updatedAtUtc.AddMinutes(-2),
                    UpdatedAtUtc = updatedAtUtc,
                    Revision = revision,
                    IsDeleted = isTombstone
                }
            }
        };

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetSession(
            "test-token",
            new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = "transfer-pull-notification-admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            },
            DateTime.UtcNow.AddDays(1));
        return session;
    }

    private static Task InvokeApplyPullAsync(
        SyncService sync,
        SyncPullResponse pull,
        bool updateSyncRevision = false)
    {
        var method = typeof(SyncService).GetMethod(
                         "ApplyPullAsync",
                         BindingFlags.Instance | BindingFlags.NonPublic) ??
                     throw new MissingMethodException(
                         nameof(SyncService),
                         "ApplyPullAsync");
        return (Task)method.Invoke(
            sync,
            [pull, 0L, CancellationToken.None, updateSyncRevision])!;
    }
}
