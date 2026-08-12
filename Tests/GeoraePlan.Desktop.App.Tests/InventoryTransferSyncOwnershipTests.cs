using System.Collections.Specialized;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InventoryTransferSyncOwnershipTests
{
    [Fact]
    public async Task DirtyTransferSelection_FollowsPendingFinalAndDeleteOwnershipMatrix()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-sync-ownership");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var transferId = Guid.NewGuid();
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            TransferNumber = "TR-SYNC-OWNERSHIP",
            TransferDate = new DateOnly(2026, 8, 1),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            IsDirty = true
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sourceSession = CreateUserSession(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeOfficeOnly);
        var targetSession = CreateUserSession(
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly);
        var tenantWideSession = CreateUserSession(
            OfficeCodeCatalog.Usenet,
            TenantScopeCatalog.ScopeTenantAll);
        var globalAdminSession = CreateGlobalAdminSession();
        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            sourceSession);

        await AssertRoutingAsync(
            sourceExpected: true,
            targetExpected: false,
            tenantWideExpected: true,
            expectedSummaryOffices: [OfficeCodeCatalog.Usenet]);

        await UpdateTransferAsync(transfer =>
        {
            transfer.IsDeleted = true;
        });
        await AssertRoutingAsync(
            sourceExpected: true,
            targetExpected: false,
            tenantWideExpected: true,
            expectedSummaryOffices: [OfficeCodeCatalog.Usenet]);

        await UpdateTransferAsync(transfer =>
        {
            transfer.IsDeleted = false;
            transfer.TransferStatus = string.Empty;
            transfer.ReceivedByUsername = "receiver";
            transfer.ReceivedAtUtc = new DateTime(2026, 8, 1, 1, 0, 0, DateTimeKind.Utc);
        });
        await AssertRoutingAsync(
            sourceExpected: false,
            targetExpected: false,
            tenantWideExpected: true,
            expectedSummaryOffices:
            [
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Yeonsu
            ]);

        await UpdateTransferAsync(transfer =>
        {
            transfer.Revision = 9;
        });
        await AssertRoutingAsync(
            sourceExpected: false,
            targetExpected: true,
            tenantWideExpected: true,
            expectedSummaryOffices: [OfficeCodeCatalog.Yeonsu]);

        await UpdateTransferAsync(transfer =>
        {
            transfer.IsDeleted = true;
        });
        await AssertRoutingAsync(
            sourceExpected: false,
            targetExpected: false,
            tenantWideExpected: true,
            expectedSummaryOffices:
            [
                OfficeCodeCatalog.Usenet,
                OfficeCodeCatalog.Yeonsu
            ]);

        async Task UpdateTransferAsync(Action<LocalInventoryTransfer> update)
        {
            var transfer = await db.InventoryTransfers
                .IgnoreQueryFilters()
                .SingleAsync(current => current.Id == transferId);
            update(transfer);
            transfer.IsDirty = true;
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        async Task AssertRoutingAsync(
            bool sourceExpected,
            bool targetExpected,
            bool tenantWideExpected,
            IReadOnlyCollection<string> expectedSummaryOffices)
        {
            Assert.Equal(
                sourceExpected,
                (await service.GetDirtyInventoryTransfersForSyncAsync(sourceSession))
                .Any(transfer => transfer.Id == transferId));
            Assert.Equal(
                targetExpected,
                (await service.GetDirtyInventoryTransfersForSyncAsync(targetSession))
                .Any(transfer => transfer.Id == transferId));
            Assert.Equal(
                tenantWideExpected,
                (await service.GetDirtyInventoryTransfersForSyncAsync(tenantWideSession))
                .Any(transfer => transfer.Id == transferId));
            Assert.Contains(
                await service.GetDirtyInventoryTransfersForSyncAsync(globalAdminSession),
                transfer => transfer.Id == transferId);

            var summaries = await service.GetDirtyOfficeSummariesAsync();
            Assert.Equal(
                expectedSummaryOffices.OrderBy(code => code, StringComparer.OrdinalIgnoreCase),
                summaries
                    .Select(summary => summary.OfficeCode)
                    .OrderBy(code => code, StringComparer.OrdinalIgnoreCase));
            Assert.All(summaries, summary => Assert.Equal(1, summary.Count));
        }
    }

    [Theory]
    [InlineData("confirm")]
    [InlineData("reject")]
    public async Task UnsyncedPendingTransfer_TargetCannotFinalizeBeforeSourceSync(
        string operation)
    {
        using var appRoot = new LocalAppRootScope(
            $"georaeplan-transfer-unsynced-final-{operation}");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var transferId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        db.Items.Add(new LocalItem
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "Unsynced final guard item",
            NameMatchKey = "UNSYNCEDFINALGUARDITEM",
            Unit = "EA",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.NonStock,
            IsDirty = false
        });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            Revision = 0,
            TransferNumber = "TR-UNSYNCED-FINAL-GUARD",
            TransferDate = new DateOnly(2026, 8, 2),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            IsDirty = true,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Unsynced final guard item",
                    Unit = "EA",
                    Quantity = 1m,
                    ReceivedQuantity = 1m,
                    QuantityDifference = 0m
                }
            ]
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var targetSession = CreateUserSession(
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly);
        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            targetSession);

        var result = string.Equals(operation, "confirm", StringComparison.Ordinal)
            ? await service.ConfirmInventoryTransferReceiptAsync(
                transferId,
                [new LocalInventoryTransferLine
                {
                    Id = lineId,
                    ReceivedQuantity = 1m
                }],
                "must wait for source sync",
                targetSession,
                expectedRevision: 0)
            : await service.RejectInventoryTransferAsync(
                transferId,
                "must wait for source sync",
                targetSession,
                expectedRevision: 0);

        Assert.False(result.Success);
        Assert.True(result.PermissionDenied);
        Assert.Contains("먼저 동기화", result.Message, StringComparison.Ordinal);
        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == transferId);
        Assert.Equal(InventoryTransferStatusNormalizer.Pending, stored.TransferStatus);
        Assert.Equal(0, stored.Revision);
        Assert.True(stored.IsDirty);
        Assert.False(await db.AuditLogs.AnyAsync(log =>
            log.EntityId == transferId.ToString("D") &&
            (log.Action == "ConfirmReceipt" || log.Action == "Reject")));
    }

    [Fact]
    public async Task InventoryTransferViewModel_ReceiptCommandsWaitForServerRevisionUnlessUserCanWriteBothOffices()
    {
        using var appRoot = new LocalAppRootScope(
            "georaeplan-transfer-unsynced-final-viewmodel");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var transferId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        db.Items.Add(new LocalItem
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "Unsynced viewmodel item",
            NameMatchKey = "UNSYNCEDVIEWMODELITEM",
            Unit = "EA",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.NonStock,
            IsDirty = false
        });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            Revision = 0,
            TransferNumber = "TR-UNSYNCED-VIEWMODEL",
            TransferDate = new DateOnly(2026, 8, 2),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            IsDirty = true,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = Guid.NewGuid(),
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Unsynced viewmodel item",
                    Unit = "EA",
                    Quantity = 1m,
                    ReceivedQuantity = 1m
                }
            ]
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var targetSession = CreateUserSession(
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly);
        using var targetViewModel = new InventoryTransferViewModel(
            new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                targetSession),
            targetSession);
        await targetViewModel.LoadAsync(
            new LocalInventoryTransfer { Id = transferId });

        Assert.True(targetViewModel.CanCurrentUserReceive);
        Assert.False(targetViewModel.CanEditReceiptDraft);
        Assert.False(targetViewModel.CanConfirmReceipt);
        Assert.False(targetViewModel.CanRejectTransfer);

        var bothOfficeSession = CreateUserSession(
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeTenantAll);
        using var bothOfficeViewModel = new InventoryTransferViewModel(
            new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                bothOfficeSession),
            bothOfficeSession);
        await bothOfficeViewModel.LoadAsync(
            new LocalInventoryTransfer { Id = transferId });

        Assert.True(bothOfficeViewModel.CanEditReceiptDraft);
        Assert.True(bothOfficeViewModel.CanConfirmReceipt);
        Assert.True(bothOfficeViewModel.CanRejectTransfer);
    }

    [Fact]
    public async Task ExplicitSaveAndReload_ReconcileSelectionWithoutDuplicateWrites()
    {
        using var appRoot = new LocalAppRootScope("georaeplan-transfer-refresh-selection");
        await using var db = CreateDbContext(appRoot.DbPath);
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();

        var itemId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        db.Warehouses.AddRange(
            new LocalWarehouse
            {
                OfficeCode = OfficeCodeCatalog.Usenet,
                Code = OfficeCodeCatalog.UsenetMainWarehouse,
                Name = "Source warehouse",
                IsActive = true,
                IsDirty = false
            },
            new LocalWarehouse
            {
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                Code = OfficeCodeCatalog.YeonsuMainWarehouse,
                Name = "Target warehouse",
                IsActive = true,
                IsDirty = false
            });
        db.Items.Add(new LocalItem
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Shared,
            NameOriginal = "Transfer runtime item",
            NameMatchKey = "TRANSFERRUNTIMEITEM",
            TrackingType = ItemTrackingTypes.NonStock,
            Unit = "EA",
            IsDirty = false
        });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            Revision = 7,
            TransferNumber = "TR-SELECTION-RESET",
            TransferDate = new DateOnly(2026, 8, 1),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            Memo = "before",
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Transfer runtime item",
                    Unit = "EA",
                    Quantity = 1m
                }
            ]
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var session = CreateGlobalAdminSession();
        var dispatcher = new SyncRequestDispatcher();
        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            dispatcher,
            session);
        using var viewModel = new InventoryTransferViewModel(service, session);
        await viewModel.LoadAsync(new LocalInventoryTransfer { Id = transferId });
        Assert.Equal(transferId, viewModel.SelectedTransfer?.Id);

        var observedBindingReset = false;
        var observedBindingReplace = false;
        NotifyCollectionChangedEventHandler bindingResetHandler = (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
                observedBindingReset = true;
            else if (args.Action == NotifyCollectionChangedAction.Replace)
                observedBindingReplace = true;
            else
                return;

            viewModel.SelectedTransfer = null;
        };
        viewModel.Transfers.CollectionChanged += bindingResetHandler;

        const string updatedMemo = "single explicit update";
        viewModel.Memo = updatedMemo;
        Assert.True(viewModel.HasPendingChanges);

        await viewModel.SaveTransferCommand.ExecuteAsync(null);
        await Task.Delay(TimeSpan.FromMilliseconds(750));

        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers
            .AsNoTracking()
            .SingleAsync(transfer => transfer.Id == transferId);
        var updateCount = await db.AuditLogs
            .AsNoTracking()
            .CountAsync(log =>
                log.EntityName == "LocalInventoryTransfer" &&
                log.EntityId == transferId.ToString("D") &&
                log.Action == "Update");

        Assert.True(observedBindingReset);
        Assert.True(observedBindingReplace);
        Assert.Equal(updatedMemo, stored.Memo);
        Assert.Equal(1, updateCount);
        Assert.Equal(transferId, viewModel.SelectedTransfer?.Id);
        Assert.False(viewModel.HasPendingChanges);

        const string remoteWinnerMemo = "remote winner";
        var remoteWinnerRevision = stored.Revision + 1;
        var remoteWinner = await db.InventoryTransfers
            .SingleAsync(transfer => transfer.Id == transferId);
        remoteWinner.Revision = remoteWinnerRevision;
        remoteWinner.Memo = remoteWinnerMemo;
        remoteWinner.IsDirty = false;
        remoteWinner.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var staleSelection = viewModel.SelectedTransfer;
        Assert.NotNull(staleSelection);
        Assert.NotEqual(remoteWinnerRevision, staleSelection.Revision);

        await viewModel.OpenTransferAsync(transferId);

        Assert.NotSame(staleSelection, viewModel.SelectedTransfer);
        Assert.Equal(transferId, viewModel.SelectedTransfer?.Id);
        Assert.Equal(remoteWinnerRevision, viewModel.SelectedTransfer?.Revision);
        Assert.Equal(remoteWinnerMemo, viewModel.SelectedTransfer?.Memo);
        Assert.Equal(remoteWinnerMemo, viewModel.Memo);
        Assert.False(viewModel.HasPendingChanges);

        const string retryMemo = "retry after explicit reload";
        viewModel.Memo = retryMemo;
        await viewModel.SaveTransferCommand.ExecuteAsync(null);
        await Task.Delay(TimeSpan.FromMilliseconds(750));

        db.ChangeTracker.Clear();
        var retried = await db.InventoryTransfers
            .AsNoTracking()
            .SingleAsync(transfer => transfer.Id == transferId);
        var finalUpdateCount = await db.AuditLogs
            .AsNoTracking()
            .CountAsync(log =>
                log.EntityName == "LocalInventoryTransfer" &&
                log.EntityId == transferId.ToString("D") &&
                log.Action == "Update");

        Assert.Equal(retryMemo, retried.Memo);
        Assert.Equal(remoteWinnerRevision, retried.Revision);
        Assert.Equal(2, finalUpdateCount);
        Assert.Equal(remoteWinnerRevision, viewModel.SelectedTransfer?.Revision);
        Assert.False(viewModel.HasPendingChanges);

        var deleted = await db.InventoryTransfers
            .SingleAsync(transfer => transfer.Id == transferId);
        deleted.IsDeleted = true;
        deleted.IsDirty = false;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await viewModel.OpenTransferAsync(transferId);
        viewModel.Transfers.CollectionChanged -= bindingResetHandler;

        Assert.Null(viewModel.SelectedTransfer);
        Assert.Equal(Guid.Empty, viewModel.TransferId);
        Assert.DoesNotContain(
            viewModel.Transfers,
            transfer => transfer.Id == transferId);
        Assert.False(viewModel.HasPendingChanges);
    }

    [Fact]
    public async Task ExternalRefresh_CleanEditorAppliesLatestTransferWithoutLocalWrite()
    {
        await using var fixture = await CreateLoadedTransferFixtureAsync(
            "georaeplan-transfer-clean-external-refresh");
        var viewModel = fixture.ViewModel;
        var db = fixture.Db;
        var staleSelection = viewModel.SelectedTransfer;
        var auditCountBefore = await db.AuditLogs.AsNoTracking().CountAsync();

        const string remoteMemo = "remote clean winner";
        var remoteRevision = await ApplyRemoteTransferChangeAsync(
            db,
            fixture.TransferId,
            transfer => transfer.Memo = remoteMemo);

        await viewModel.HandleInventoryStateChangedAsync();

        Assert.NotSame(staleSelection, viewModel.SelectedTransfer);
        Assert.Equal(fixture.TransferId, viewModel.TransferId);
        Assert.Equal(remoteRevision, viewModel.SelectedTransfer?.Revision);
        Assert.Equal(remoteMemo, viewModel.SelectedTransfer?.Memo);
        Assert.Equal(remoteMemo, viewModel.Memo);
        Assert.False(viewModel.HasPendingChanges);
        Assert.False(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.IsExternalTransferUnavailable);
        Assert.Equal(
            auditCountBefore,
            await db.AuditLogs.AsNoTracking().CountAsync());

        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers
            .AsNoTracking()
            .SingleAsync(transfer => transfer.Id == fixture.TransferId);
        Assert.False(stored.IsDirty);
        Assert.Equal(remoteMemo, stored.Memo);
    }

    [Fact]
    public async Task ExternalRefresh_NewVisibleTransferAddsItToOpenListWithoutReplacingEditor()
    {
        await using var fixture = await CreateLoadedTransferFixtureAsync(
            "georaeplan-transfer-new-visible-external-refresh");
        var viewModel = fixture.ViewModel;
        var db = fixture.Db;
        var selectedTransfer = viewModel.SelectedTransfer;
        var editorMemo = viewModel.Memo;
        var remoteTransferId = await AddRemoteTransferAsync(
            db,
            fixture.ItemId,
            "TR-REMOTE-NEW");

        await viewModel.HandleInventoryStateChangedAsync();

        var listedRemote = Assert.Single(
            viewModel.Transfers,
            transfer => transfer.Id == remoteTransferId);
        Assert.Equal("TR-REMOTE-NEW", listedRemote.TransferNumber);
        Assert.False(listedRemote.IsDirty);
        Assert.NotSame(selectedTransfer, viewModel.SelectedTransfer);
        Assert.Equal(fixture.TransferId, viewModel.SelectedTransfer?.Id);
        Assert.Equal(fixture.TransferId, viewModel.TransferId);
        Assert.Equal(editorMemo, viewModel.Memo);
        Assert.False(viewModel.HasPendingChanges);
        Assert.False(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.IsExternalTransferUnavailable);
    }

    [Fact]
    public async Task ExternalRefresh_BusyNotificationReplaysNewVisibleTransferAfterIdle()
    {
        await using var fixture = await CreateLoadedTransferFixtureAsync(
            "georaeplan-transfer-busy-external-refresh");
        var viewModel = fixture.ViewModel;
        var remoteTransferId = await AddRemoteTransferAsync(
            fixture.Db,
            fixture.ItemId,
            "TR-REMOTE-BUSY");

        viewModel.IsBusy = true;
        await viewModel.HandleInventoryStateChangedAsync();

        Assert.DoesNotContain(
            viewModel.Transfers,
            transfer => transfer.Id == remoteTransferId);

        viewModel.IsBusy = false;
        var deadlineUtc = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadlineUtc &&
               viewModel.Transfers.All(
                   transfer => transfer.Id != remoteTransferId))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        var listedRemote = Assert.Single(
            viewModel.Transfers,
            transfer => transfer.Id == remoteTransferId);
        Assert.Equal("TR-REMOTE-BUSY", listedRemote.TransferNumber);
        Assert.Equal(fixture.TransferId, viewModel.TransferId);
        Assert.Equal(fixture.TransferId, viewModel.SelectedTransfer?.Id);
        Assert.False(viewModel.HasPendingChanges);
        Assert.False(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.IsExternalTransferUnavailable);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task ExternalRefresh_DirtyEditorPreservesDraftAndOldRevisionConflict()
    {
        await using var fixture = await CreateLoadedTransferFixtureAsync(
            "georaeplan-transfer-dirty-external-refresh");
        var viewModel = fixture.ViewModel;
        var db = fixture.Db;
        var originalRevision = viewModel.SelectedTransfer!.Revision;

        viewModel.Memo = "local unsaved memo";
        viewModel.SelectedLine = Assert.Single(viewModel.Lines);
        viewModel.InputQty = 3m;
        viewModel.InputRemark = "local unsaved line remark";
        Assert.True(viewModel.HasPendingChanges);

        const string remoteMemo = "remote dirty winner";
        var remoteRevision = await ApplyRemoteTransferChangeAsync(
            db,
            fixture.TransferId,
            transfer => transfer.Memo = remoteMemo);
        Assert.True(remoteRevision > originalRevision);

        await viewModel.HandleInventoryStateChangedAsync();

        Assert.Equal(fixture.TransferId, viewModel.TransferId);
        Assert.Equal(remoteRevision, viewModel.SelectedTransfer?.Revision);
        Assert.Equal(remoteMemo, viewModel.SelectedTransfer?.Memo);
        Assert.Equal("local unsaved memo", viewModel.Memo);
        Assert.Equal(3m, viewModel.InputQty);
        Assert.Equal("local unsaved line remark", viewModel.InputRemark);
        Assert.True(viewModel.HasPendingChanges);
        Assert.True(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.IsExternalTransferUnavailable);
        Assert.Contains("기존 기준 버전", viewModel.StatusMessage, StringComparison.Ordinal);

        Assert.False(await viewModel.TryAutoSaveOnCloseAsync());

        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers
            .AsNoTracking()
            .SingleAsync(transfer => transfer.Id == fixture.TransferId);
        Assert.Equal(remoteRevision, stored.Revision);
        Assert.Equal(remoteMemo, stored.Memo);
        Assert.False(stored.IsDirty);
    }

    [Fact]
    public async Task ExternalRefresh_PendingEditMatchingLatestConvergesWithoutConflict()
    {
        await using var fixture = await CreateLoadedTransferFixtureAsync(
            "georaeplan-transfer-matching-external-refresh");
        var viewModel = fixture.ViewModel;
        var db = fixture.Db;

        const string matchingMemo = "same edit on both PCs";
        viewModel.Memo = matchingMemo;
        Assert.True(viewModel.HasPendingChanges);

        var remoteRevision = await ApplyRemoteTransferChangeAsync(
            db,
            fixture.TransferId,
            transfer => transfer.Memo = matchingMemo);

        await viewModel.HandleInventoryStateChangedAsync();

        Assert.Equal(fixture.TransferId, viewModel.TransferId);
        Assert.Equal(remoteRevision, viewModel.SelectedTransfer?.Revision);
        Assert.Equal(matchingMemo, viewModel.SelectedTransfer?.Memo);
        Assert.Equal(matchingMemo, viewModel.Memo);
        Assert.False(viewModel.HasPendingChanges);
        Assert.False(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.IsExternalTransferUnavailable);

        db.ChangeTracker.Clear();
        var stored = await db.InventoryTransfers
            .AsNoTracking()
            .SingleAsync(transfer => transfer.Id == fixture.TransferId);
        Assert.Equal(remoteRevision, stored.Revision);
        Assert.Equal(matchingMemo, stored.Memo);
        Assert.False(stored.IsDirty);
    }

    [Fact]
    public async Task RetrySave_ThenServerConfirmationLeavesCleanEditorBaseline()
    {
        await using var fixture = await CreateLoadedTransferFixtureAsync(
            "georaeplan-transfer-retry-confirmation-refresh");
        var viewModel = fixture.ViewModel;
        var db = fixture.Db;

        viewModel.Memo = "stale local draft";
        _ = await ApplyRemoteTransferChangeAsync(
            db,
            fixture.TransferId,
            transfer => transfer.Memo = "remote winner");
        await viewModel.HandleInventoryStateChangedAsync();

        Assert.True(viewModel.HasPendingChanges);
        Assert.True(viewModel.HasExternalTransferConflict);

        await viewModel.DiscardDraftAndReloadLatestTransferAsync();
        viewModel.Memo = "retry winner";
        await viewModel.SaveTransferCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasPendingChanges);
        Assert.False(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.IsExternalTransferUnavailable);
        Assert.Equal("retry winner", viewModel.Memo);
        Assert.Equal("retry winner", viewModel.SelectedTransfer?.Memo);
        Assert.True(viewModel.SelectedTransfer?.IsDirty);

        db.ChangeTracker.Clear();
        var confirmed = await db.InventoryTransfers
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == fixture.TransferId);
        confirmed.IsDirty = false;
        confirmed.Revision++;
        confirmed.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1);
        foreach (var line in confirmed.Lines)
        {
            line.Quantity = decimal.Parse(
                "1.0",
                System.Globalization.CultureInfo.InvariantCulture);
            line.ReceivedQuantity = decimal.Parse(
                "1.00",
                System.Globalization.CultureInfo.InvariantCulture);
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await viewModel.HandleInventoryStateChangedAsync();

        Assert.False(viewModel.HasPendingChanges);
        Assert.False(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.IsExternalTransferUnavailable);
        Assert.Equal(confirmed.Revision, viewModel.SelectedTransfer?.Revision);
        Assert.False(viewModel.SelectedTransfer?.IsDirty);
        Assert.Equal("retry winner", viewModel.Memo);
        Assert.Equal("retry winner", viewModel.SelectedTransfer?.Memo);
    }

    [Fact]
    public async Task ExternalRefresh_WarehouseLookupResetDoesNotCreatePhantomRouteDraft()
    {
        await using var fixture = await CreateLoadedTransferFixtureAsync(
            "georaeplan-transfer-warehouse-lookup-reset");
        var viewModel = fixture.ViewModel;
        var expectedFromWarehouseCode = viewModel.FromWarehouseCode;
        var expectedToWarehouseCode = viewModel.ToWarehouseCode;
        var resetCount = 0;

        NotifyCollectionChangedEventHandler bindingResetHandler = (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Reset)
                return;

            resetCount++;
            viewModel.FromWarehouseCode = string.Empty;
            viewModel.ToWarehouseCode = string.Empty;
        };

        viewModel.Warehouses.CollectionChanged += bindingResetHandler;
        try
        {
            await viewModel.HandleInventoryStateChangedAsync();
        }
        finally
        {
            viewModel.Warehouses.CollectionChanged -= bindingResetHandler;
        }

        Assert.True(resetCount > 0);
        Assert.Equal(expectedFromWarehouseCode, viewModel.FromWarehouseCode);
        Assert.Equal(expectedToWarehouseCode, viewModel.ToWarehouseCode);
        Assert.False(viewModel.HasPendingChanges);
        Assert.False(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.IsExternalTransferUnavailable);
    }

    [Fact]
    public async Task ExternalRefresh_CleanRemoteDeleteRemovesSelectionAndStartsNewDocument()
    {
        await using var fixture = await CreateLoadedTransferFixtureAsync(
            "georaeplan-transfer-clean-remote-delete");
        var viewModel = fixture.ViewModel;

        await ApplyRemoteTransferChangeAsync(
            fixture.Db,
            fixture.TransferId,
            transfer => transfer.IsDeleted = true);

        await viewModel.HandleInventoryStateChangedAsync();

        Assert.Null(viewModel.SelectedTransfer);
        Assert.Equal(Guid.Empty, viewModel.TransferId);
        Assert.DoesNotContain(
            viewModel.Transfers,
            transfer => transfer.Id == fixture.TransferId);
        Assert.False(viewModel.HasPendingChanges);
        Assert.False(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.IsExternalTransferUnavailable);
        Assert.Contains("새 문서 화면", viewModel.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalRefresh_DirtyRemoteDeletePreservesDraftAndCannotResurrectTombstone()
    {
        await using var fixture = await CreateLoadedTransferFixtureAsync(
            "georaeplan-transfer-dirty-remote-delete");
        var viewModel = fixture.ViewModel;
        var db = fixture.Db;

        viewModel.Memo = "orphan draft must survive";
        viewModel.SelectedLine = Assert.Single(viewModel.Lines);
        viewModel.InputQty = 4m;
        viewModel.InputReceiptRemark = "orphan line draft";
        Assert.True(viewModel.HasPendingChanges);

        var tombstoneRevision = await ApplyRemoteTransferChangeAsync(
            db,
            fixture.TransferId,
            transfer => transfer.IsDeleted = true);

        await viewModel.HandleInventoryStateChangedAsync();

        Assert.Equal(fixture.TransferId, viewModel.TransferId);
        Assert.Null(viewModel.SelectedTransfer);
        Assert.Equal("orphan draft must survive", viewModel.Memo);
        Assert.Equal(4m, viewModel.InputQty);
        Assert.Equal("orphan line draft", viewModel.InputReceiptRemark);
        Assert.True(viewModel.HasPendingChanges);
        Assert.True(viewModel.HasExternalTransferConflict);
        Assert.True(viewModel.IsExternalTransferUnavailable);
        Assert.False(viewModel.CanSaveTransfer);
        Assert.False(viewModel.CanDeleteTransfer);
        Assert.False(viewModel.CanConfirmReceipt);
        Assert.False(viewModel.CanRejectTransfer);
        Assert.Contains("다시 생기지 않도록", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.False(await viewModel.TryAutoSaveOnCloseAsync());

        db.ChangeTracker.Clear();
        var tombstone = await db.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transfer => transfer.Id == fixture.TransferId);
        Assert.True(tombstone.IsDeleted);
        Assert.False(tombstone.IsDirty);
        Assert.Equal(tombstoneRevision, tombstone.Revision);
        Assert.NotEqual("orphan draft must survive", tombstone.Memo);

        await viewModel.DiscardDraftAndReloadLatestTransferAsync();

        Assert.Equal(Guid.Empty, viewModel.TransferId);
        Assert.Null(viewModel.SelectedTransfer);
        Assert.False(viewModel.HasPendingChanges);
        Assert.False(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.IsExternalTransferUnavailable);
    }

    [Fact]
    public async Task Restart_PersistedRemoteTombstoneConflictRestoresDraftAndBlocksMutations()
    {
        await using var fixture =
            await CreatePersistedTombstoneConflictFixtureAsync(
                "georaeplan-transfer-tombstone-conflict-restart");
        var viewModel = fixture.ViewModel;
        var expectedDraft = fixture.LocalDraft;

        var listedDraft = Assert.Single(
            viewModel.Transfers,
            transfer => transfer.Id == fixture.TransferId);
        Assert.Equal(expectedDraft.TransferNumber, listedDraft.TransferNumber);
        Assert.Equal(expectedDraft.Revision, listedDraft.Revision);
        Assert.True(listedDraft.IsDirty);
        Assert.False(listedDraft.IsDeleted);

        Assert.Equal(fixture.TransferId, viewModel.TransferId);
        Assert.Equal(fixture.TransferId, viewModel.SelectedTransfer?.Id);
        Assert.Equal(expectedDraft.TransferNumber, viewModel.TransferNumber);
        Assert.Equal(expectedDraft.Memo, viewModel.Memo);
        var editorLine = Assert.Single(viewModel.Lines);
        var expectedLine = Assert.Single(expectedDraft.Lines);
        Assert.Equal(expectedLine.Id, editorLine.Id);
        Assert.Equal(expectedLine.ItemId, editorLine.ItemId);
        Assert.Equal(expectedLine.Quantity, editorLine.Quantity);
        Assert.Equal(expectedLine.ReceivedQuantity, editorLine.ReceivedQuantity);
        Assert.Equal(expectedLine.Remark, editorLine.Remark);
        Assert.Equal(expectedLine.ReceiptRemark, editorLine.ReceiptRemark);

        Assert.True(viewModel.IsExternalTransferUnavailable);
        Assert.True(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.CanSaveTransfer);
        Assert.False(viewModel.CanDeleteTransfer);
        Assert.False(viewModel.CanConfirmReceipt);
        Assert.False(viewModel.CanRejectTransfer);
        Assert.False(viewModel.CanEditTransferDraft);
        Assert.True(viewModel.CanRecoverRemoteDeletedTransferAsNew);

        var availableItem = await fixture.Db.Items
            .AsNoTracking()
            .SingleAsync(item => item.Id == expectedLine.ItemId);
        viewModel.ApplyInputItem(availableItem);
        viewModel.SelectedLine = editorLine;
        Assert.False(viewModel.CanAddLine);
        Assert.False(viewModel.CanUpdateLine);
        Assert.False(viewModel.CanDeleteLine);

        viewModel.Memo = $"{expectedDraft.Memo} plus unsaved edit";
        Assert.True(viewModel.HasPendingChanges);
        Assert.False(await viewModel.TryAutoSaveOnCloseAsync());

        await viewModel.SaveTransferCommand.ExecuteAsync(null);
        await viewModel.DeleteCurrentTransferAsync();
        await viewModel.ConfirmReceiptCommand.ExecuteAsync(null);
        await viewModel.RejectTransferCommand.ExecuteAsync(null);

        fixture.Db.ChangeTracker.Clear();
        var serverTombstone = await fixture.Db.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transfer => transfer.Id == fixture.TransferId);
        var persistedConflict = await fixture.Db
            .InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .SingleAsync(conflict => conflict.TransferId == fixture.TransferId);
        var failedOutbox = await fixture.Db.SyncOutboxEntries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == fixture.OutboxId);

        Assert.True(serverTombstone.IsDeleted);
        Assert.False(serverTombstone.IsDirty);
        Assert.Equal(fixture.ServerRevision, serverTombstone.Revision);
        Assert.Equal(
            InventoryTransferTombstoneConflictPolicy.UnresolvedStatus,
            persistedConflict.Status);
        Assert.Equal(string.Empty, persistedConflict.Resolution);
        Assert.Null(persistedConflict.ResolvedAtUtc);
        Assert.Equal("Failed", failedOutbox.Status);
        Assert.StartsWith(
            InventoryTransferTombstoneConflictPolicy.OutboxErrorPrefix,
            failedOutbox.ErrorMessage,
            StringComparison.Ordinal);
        Assert.Null(failedOutbox.AcknowledgedAtUtc);
    }

    [Fact]
    public async Task DiscardPersistedRemoteTombstoneDraft_ResolvesConflictAndStartsNewDocument()
    {
        await using var fixture =
            await CreatePersistedTombstoneConflictFixtureAsync(
                "georaeplan-transfer-tombstone-conflict-discard");
        var viewModel = fixture.ViewModel;

        await viewModel.DiscardDraftAndReloadLatestTransferAsync();

        Assert.Equal(Guid.Empty, viewModel.TransferId);
        Assert.Null(viewModel.SelectedTransfer);
        Assert.Empty(viewModel.Lines);
        Assert.Equal(string.Empty, viewModel.TransferNumber);
        Assert.DoesNotContain(
            viewModel.Transfers,
            transfer => transfer.Id == fixture.TransferId);
        Assert.False(viewModel.HasPendingChanges);
        Assert.False(viewModel.IsExternalTransferUnavailable);
        Assert.False(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.CanRecoverRemoteDeletedTransferAsNew);

        fixture.Db.ChangeTracker.Clear();
        var persistedConflict = await fixture.Db
            .InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .SingleAsync(conflict => conflict.TransferId == fixture.TransferId);
        var acknowledgedOutbox = await fixture.Db.SyncOutboxEntries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == fixture.OutboxId);
        var serverTombstone = await fixture.Db.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transfer => transfer.Id == fixture.TransferId);

        Assert.Equal(
            InventoryTransferTombstoneConflictPolicy.ResolvedStatus,
            persistedConflict.Status);
        Assert.Equal("Discarded", persistedConflict.Resolution);
        Assert.NotNull(persistedConflict.ResolvedAtUtc);
        Assert.Equal("Acknowledged", acknowledgedOutbox.Status);
        Assert.NotNull(acknowledgedOutbox.AcknowledgedAtUtc);
        Assert.Equal(fixture.ServerRevision, acknowledgedOutbox.AcceptedRevision);
        Assert.Equal(
            fixture.ServerUpdatedAtUtc,
            acknowledgedOutbox.AcceptedUpdatedAtUtc);
        Assert.Equal(string.Empty, acknowledgedOutbox.ErrorMessage);
        Assert.True(serverTombstone.IsDeleted);
        Assert.False(serverTombstone.IsDirty);
        Assert.Equal(fixture.ServerRevision, serverTombstone.Revision);
        Assert.Equal(
            1,
            await fixture.Db.InventoryTransfers
                .IgnoreQueryFilters()
                .CountAsync());
    }

    [Fact]
    public async Task DiscardPersistedRemoteTombstoneDraft_DeletesConflictOwnedEvidenceAtomically()
    {
        await using var fixture =
            await CreatePersistedTombstoneConflictFixtureAsync(
                "georaeplan-transfer-tombstone-conflict-discard-evidence");
        var evidencePath = Path.Combine(
            AppPaths.TransactionAttachmentsDir,
            fixture.TransferId.ToString("N"),
            "conflict-owned-evidence.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        var expected = RandomNumberGenerator.GetBytes(2048);
        await File.WriteAllBytesAsync(evidencePath, expected);
        var conflict = await fixture.Db.InventoryTransferTombstoneConflicts
            .SingleAsync(current => current.TransferId == fixture.TransferId);
        conflict.ArchivedReceiveEvidencePath = evidencePath;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var resolved = await fixture.Service
            .ResolveInventoryTransferTombstoneConflictAsync(
                fixture.TransferId,
                InventoryTransferTombstoneConflictPolicy.DiscardedResolution,
                fixture.Session);

        Assert.True(resolved);
        Assert.False(File.Exists(evidencePath));
        var persisted = await fixture.Db.InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .SingleAsync(current => current.TransferId == fixture.TransferId);
        Assert.Equal(string.Empty, persisted.ArchivedReceiveEvidencePath);
        Assert.Equal(
            InventoryTransferTombstoneConflictPolicy.ResolvedStatus,
            persisted.Status);
    }

    [Fact]
    public async Task RecoverPersistedRemoteTombstoneDraftAsNew_PreservesSourceFieldsAndResetsReceiptDefaults()
    {
        await using var fixture =
            await CreatePersistedTombstoneConflictFixtureAsync(
                "georaeplan-transfer-tombstone-conflict-recover");
        var viewModel = fixture.ViewModel;
        var expectedDraft = fixture.LocalDraft;
        var originalLine = Assert.Single(expectedDraft.Lines);

        Assert.True(viewModel.CanRecoverRemoteDeletedTransferAsNew);
        await viewModel.RecoverRemoteDeletedTransferAsNewCommand
            .ExecuteAsync(null);

        var recoveredTransferId = viewModel.TransferId;
        Assert.NotEqual(Guid.Empty, recoveredTransferId);
        Assert.NotEqual(fixture.TransferId, recoveredTransferId);
        Assert.Equal(recoveredTransferId, viewModel.SelectedTransfer?.Id);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.TransferNumber));
        Assert.NotEqual(expectedDraft.TransferNumber, viewModel.TransferNumber);
        Assert.Equal(0, viewModel.SelectedTransfer?.Revision);
        Assert.True(viewModel.SelectedTransfer?.IsDirty);
        Assert.Equal(expectedDraft.TransferDate, viewModel.TransferDate);
        Assert.Equal(expectedDraft.FromWarehouseCode, viewModel.FromWarehouseCode);
        Assert.Equal(expectedDraft.ToWarehouseCode, viewModel.ToWarehouseCode);
        Assert.Equal(expectedDraft.Memo, viewModel.Memo);
        Assert.False(viewModel.IsExternalTransferUnavailable);
        Assert.False(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.CanRecoverRemoteDeletedTransferAsNew);
        Assert.DoesNotContain(
            viewModel.Transfers,
            transfer => transfer.Id == fixture.TransferId);
        Assert.Contains(
            viewModel.Transfers,
            transfer => transfer.Id == recoveredTransferId);

        fixture.Db.ChangeTracker.Clear();
        var recovered = await fixture.Db.InventoryTransfers
            .AsNoTracking()
            .Include(transfer => transfer.Lines)
            .SingleAsync(transfer => transfer.Id == recoveredTransferId);
        var recoveredLine = Assert.Single(recovered.Lines);
        var persistedConflict = await fixture.Db
            .InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .SingleAsync(conflict => conflict.TransferId == fixture.TransferId);
        var acknowledgedOutbox = await fixture.Db.SyncOutboxEntries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == fixture.OutboxId);
        var serverTombstone = await fixture.Db.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(transfer => transfer.Id == fixture.TransferId);

        Assert.False(recovered.IsDeleted);
        Assert.True(recovered.IsDirty);
        Assert.Equal(0, recovered.Revision);
        Assert.False(string.IsNullOrWhiteSpace(recovered.TransferNumber));
        Assert.NotEqual(expectedDraft.TransferNumber, recovered.TransferNumber);
        Assert.Equal(expectedDraft.TransferDate, recovered.TransferDate);
        Assert.Equal(expectedDraft.FromWarehouseCode, recovered.FromWarehouseCode);
        Assert.Equal(expectedDraft.ToWarehouseCode, recovered.ToWarehouseCode);
        Assert.Equal(expectedDraft.Memo, recovered.Memo);
        Assert.Equal(
            InventoryTransferStatusNormalizer.Pending,
            recovered.TransferStatus);
        Assert.Equal(string.Empty, recovered.ReceiveMemo);
        Assert.Equal(string.Empty, recovered.RejectReason);

        Assert.NotEqual(originalLine.Id, recoveredLine.Id);
        Assert.Equal(recoveredTransferId, recoveredLine.TransferId);
        Assert.Equal(originalLine.ItemId, recoveredLine.ItemId);
        Assert.Equal(originalLine.ItemNameOriginal, recoveredLine.ItemNameOriginal);
        Assert.Equal(
            originalLine.SpecificationOriginal,
            recoveredLine.SpecificationOriginal);
        Assert.Equal(originalLine.Unit, recoveredLine.Unit);
        Assert.Equal(originalLine.Quantity, recoveredLine.Quantity);
        Assert.Equal(recoveredLine.Quantity, recoveredLine.ReceivedQuantity);
        Assert.Equal(0m, recoveredLine.QuantityDifference);
        Assert.Equal(originalLine.Remark, recoveredLine.Remark);
        Assert.Equal(string.Empty, recoveredLine.ReceiptRemark);

        Assert.Equal(
            InventoryTransferTombstoneConflictPolicy.ResolvedStatus,
            persistedConflict.Status);
        Assert.Equal("RecoveredAsNew", persistedConflict.Resolution);
        Assert.Equal(
            recoveredTransferId,
            persistedConflict.RecoveredTransferId);
        Assert.NotNull(persistedConflict.ResolvedAtUtc);
        Assert.Equal("Acknowledged", acknowledgedOutbox.Status);
        Assert.NotNull(acknowledgedOutbox.AcknowledgedAtUtc);
        Assert.Equal(fixture.ServerRevision, acknowledgedOutbox.AcceptedRevision);
        Assert.Equal(
            fixture.ServerUpdatedAtUtc,
            acknowledgedOutbox.AcceptedUpdatedAtUtc);
        Assert.Equal(string.Empty, acknowledgedOutbox.ErrorMessage);
        Assert.True(serverTombstone.IsDeleted);
        Assert.False(serverTombstone.IsDirty);
        Assert.Equal(fixture.ServerRevision, serverTombstone.Revision);

        var replay = await fixture.Service
            .RecoverInventoryTransferTombstoneConflictAsNewAsync(
                fixture.TransferId,
                fixture.Session);
        Assert.True(replay.Success);
        Assert.Equal(recoveredTransferId, replay.EntityId);
        Assert.Equal(
            2,
            await fixture.Db.InventoryTransfers
                .IgnoreQueryFilters()
                .CountAsync());
    }

    [Fact]
    public async Task RecoverPersistedRemoteTombstoneDraftAsNew_KeepsConflictOwnedEvidence()
    {
        await using var fixture =
            await CreatePersistedTombstoneConflictFixtureAsync(
                "georaeplan-transfer-tombstone-conflict-recover-evidence");
        var evidencePath = Path.Combine(
            AppPaths.TransactionAttachmentsDir,
            fixture.TransferId.ToString("N"),
            "conflict-owned-evidence.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        var expected = RandomNumberGenerator.GetBytes(2048);
        await File.WriteAllBytesAsync(evidencePath, expected);
        var conflict = await fixture.Db.InventoryTransferTombstoneConflicts
            .SingleAsync(current => current.TransferId == fixture.TransferId);
        conflict.ArchivedReceiveEvidencePath = evidencePath;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var result = await fixture.Service
            .RecoverInventoryTransferTombstoneConflictAsNewAsync(
                fixture.TransferId,
                fixture.Session);

        Assert.True(result.Success);
        Assert.True(File.Exists(evidencePath));
        Assert.Equal(expected, await File.ReadAllBytesAsync(evidencePath));
        var recovered = await fixture.Db.InventoryTransfers
            .AsNoTracking()
            .SingleAsync(current => current.Id == result.EntityId);
        var persisted = await fixture.Db.InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .SingleAsync(current => current.TransferId == fixture.TransferId);
        Assert.Equal(string.Empty, recovered.ReceiveEvidencePath);
        Assert.Equal(evidencePath, persisted.ArchivedReceiveEvidencePath);
        Assert.Equal(result.EntityId, persisted.RecoveredTransferId);
    }

    [Fact]
    public async Task ServerActiveRestore_KeepsLatestDocumentVisibleAndPriorLocalDraftActionable()
    {
        await using var fixture =
            await CreatePersistedTombstoneConflictFixtureAsync(
                "georaeplan-transfer-tombstone-active-shadow",
                serverIsDeleted: false,
                conflictResolution:
                    InventoryTransferTombstoneConflictPolicy
                        .ServerRestoredPendingDecisionResolution);
        var viewModel = fixture.ViewModel;

        var visibleActive = Assert.Single(
            viewModel.Transfers,
            transfer => transfer.Id == fixture.TransferId);
        Assert.False(visibleActive.IsDeleted);
        Assert.False(visibleActive.IsDirty);
        Assert.Equal(
            "server active restored winner",
            visibleActive.Memo);
        Assert.Equal(visibleActive.Memo, viewModel.Memo);
        Assert.True(viewModel.HasRemoteTombstoneConflictDraft);
        Assert.True(viewModel.CanRecoverRemoteDeletedTransferAsNew);
        Assert.True(viewModel.CanEditTransferDraft);
        Assert.False(viewModel.IsExternalTransferUnavailable);
        Assert.Contains(
            "로컬 초안",
            viewModel.StatusMessage,
            StringComparison.Ordinal);

        await viewModel.RecoverRemoteDeletedTransferAsNewCommand
            .ExecuteAsync(null);

        var recoveredTransferId = viewModel.TransferId;
        Assert.NotEqual(Guid.Empty, recoveredTransferId);
        Assert.NotEqual(fixture.TransferId, recoveredTransferId);
        Assert.False(viewModel.HasRemoteTombstoneConflictDraft);

        fixture.Db.ChangeTracker.Clear();
        var originalActive = await fixture.Db.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(
                transfer => transfer.Id == fixture.TransferId);
        var recovered = await fixture.Db.InventoryTransfers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(
                transfer => transfer.Id == recoveredTransferId);
        var conflict = await fixture.Db
            .InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .SingleAsync(
                current => current.TransferId == fixture.TransferId);

        Assert.False(originalActive.IsDeleted);
        Assert.False(originalActive.IsDirty);
        Assert.Equal(fixture.ServerRevision, originalActive.Revision);
        Assert.Equal(
            "server active restored winner",
            originalActive.Memo);
        Assert.False(recovered.IsDeleted);
        Assert.True(recovered.IsDirty);
        Assert.Equal(fixture.LocalDraft.Memo, recovered.Memo);
        Assert.Equal(
            InventoryTransferTombstoneConflictPolicy.ResolvedStatus,
            conflict.Status);
        Assert.Equal(
            recoveredTransferId,
            conflict.RecoveredTransferId);
    }

    [Theory]
    [InlineData(OfficeCodeCatalog.Usenet, true)]
    [InlineData(OfficeCodeCatalog.Yeonsu, false)]
    public async Task ServerActiveRouteDiffers_ConflictActionsFollowPreservedLocalDraftSource(
        string userOfficeCode,
        bool expectedCanResolve)
    {
        var session = CreateUserSession(
            userOfficeCode,
            TenantScopeCatalog.ScopeOfficeOnly);
        await using var fixture =
            await CreatePersistedTombstoneConflictFixtureAsync(
                $"georaeplan-transfer-active-route-scope-{userOfficeCode}",
                session,
                serverIsDeleted: false,
                conflictResolution:
                    InventoryTransferTombstoneConflictPolicy
                        .ServerRestoredPendingDecisionResolution,
                serverRouteDiffers: true);
        var viewModel = fixture.ViewModel;

        Assert.Equal(
            OfficeCodeCatalog.YeonsuMainWarehouse,
            viewModel.FromWarehouseCode);
        Assert.Equal(
            OfficeCodeCatalog.UsenetMainWarehouse,
            viewModel.ToWarehouseCode);
        Assert.True(viewModel.HasRemoteTombstoneConflictDraft);
        Assert.Equal(
            expectedCanResolve,
            viewModel.CanRecoverRemoteDeletedTransferAsNew);
        Assert.Equal(
            expectedCanResolve,
            viewModel.CanReloadLatestTransfer);

        var serviceCanDiscard =
            await fixture.Service
                .ResolveInventoryTransferTombstoneConflictAsync(
                    fixture.TransferId,
                    InventoryTransferTombstoneConflictPolicy
                        .DiscardedResolution,
                    fixture.Session);
        Assert.Equal(expectedCanResolve, serviceCanDiscard);
    }

    [Fact]
    public async Task TargetReadOnlyUser_CanInspectButCannotDiscardOrRecoverPersistedConflict()
    {
        var readOnlyTargetSession = CreateReadOnlyUserSession(
            OfficeCodeCatalog.Yeonsu,
            TenantScopeCatalog.ScopeOfficeOnly);
        await using var fixture =
            await CreatePersistedTombstoneConflictFixtureAsync(
                "georaeplan-transfer-tombstone-readonly-target",
                readOnlyTargetSession);
        var viewModel = fixture.ViewModel;

        Assert.Equal(fixture.TransferId, viewModel.TransferId);
        Assert.True(viewModel.HasRemoteTombstoneConflictDraft);
        Assert.True(viewModel.IsExternalTransferUnavailable);
        Assert.False(viewModel.CanEditTransferDraft);
        Assert.False(viewModel.CanRecoverRemoteDeletedTransferAsNew);
        Assert.False(viewModel.CanReloadLatestTransfer);

        Assert.False(
            await fixture.Service
                .ResolveInventoryTransferTombstoneConflictAsync(
                    fixture.TransferId,
                    InventoryTransferTombstoneConflictPolicy
                        .DiscardedResolution,
                    fixture.Session));
        var recoverResult = await fixture.Service
            .RecoverInventoryTransferTombstoneConflictAsNewAsync(
                fixture.TransferId,
                fixture.Session);
        Assert.False(recoverResult.Success);
        Assert.True(recoverResult.PermissionDenied);

        fixture.Db.ChangeTracker.Clear();
        var conflict = await fixture.Db
            .InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .SingleAsync(
                current => current.TransferId == fixture.TransferId);
        var outbox = await fixture.Db.SyncOutboxEntries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == fixture.OutboxId);
        Assert.Equal(
            InventoryTransferTombstoneConflictPolicy.UnresolvedStatus,
            conflict.Status);
        Assert.Equal("Failed", outbox.Status);
        Assert.Null(outbox.AcknowledgedAtUtc);
    }

    [Fact]
    public async Task CapturedOutboxScopeDrift_BlocksDiscardAndRollsBackAtomicRecovery()
    {
        await using var fixture =
            await CreatePersistedTombstoneConflictFixtureAsync(
                "georaeplan-transfer-tombstone-outbox-scope-drift");
        var capturedOutbox = await fixture.Db.SyncOutboxEntries
            .SingleAsync(entry => entry.Id == fixture.OutboxId);
        capturedOutbox.ResponsibleOfficeCode =
            OfficeCodeCatalog.Usenet;
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        Assert.False(
            await fixture.Service
                .ResolveInventoryTransferTombstoneConflictAsync(
                    fixture.TransferId,
                    InventoryTransferTombstoneConflictPolicy
                        .DiscardedResolution,
                    fixture.Session));
        var recovery = await fixture.Service
            .RecoverInventoryTransferTombstoneConflictAsNewAsync(
                fixture.TransferId,
                fixture.Session);
        Assert.False(recovery.Success);
        Assert.True(recovery.ConcurrencyConflict);

        fixture.Db.ChangeTracker.Clear();
        var conflict = await fixture.Db
            .InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .SingleAsync(
                current => current.TransferId == fixture.TransferId);
        capturedOutbox = await fixture.Db.SyncOutboxEntries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == fixture.OutboxId);
        Assert.Equal(
            InventoryTransferTombstoneConflictPolicy.UnresolvedStatus,
            conflict.Status);
        Assert.Null(conflict.RecoveredTransferId);
        Assert.Equal("Failed", capturedOutbox.Status);
        Assert.Null(capturedOutbox.AcknowledgedAtUtc);
        Assert.Equal(
            1,
            await fixture.Db.InventoryTransfers
                .IgnoreQueryFilters()
                .CountAsync());
    }

    [Fact]
    public async Task MalformedCapturedOutboxList_BlocksDiscardAndRollsBackAtomicRecovery()
    {
        await using var fixture =
            await CreatePersistedTombstoneConflictFixtureAsync(
                "georaeplan-transfer-tombstone-malformed-outbox-list");
        var conflict = await fixture.Db
            .InventoryTransferTombstoneConflicts
            .SingleAsync(
                current => current.TransferId == fixture.TransferId);
        conflict.OutboxMutationIdsJson = "{ malformed";
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        Assert.False(
            await fixture.Service
                .ResolveInventoryTransferTombstoneConflictAsync(
                    fixture.TransferId,
                    InventoryTransferTombstoneConflictPolicy
                        .DiscardedResolution,
                    fixture.Session));
        var recovery = await fixture.Service
            .RecoverInventoryTransferTombstoneConflictAsNewAsync(
                fixture.TransferId,
                fixture.Session);
        Assert.False(recovery.Success);
        Assert.True(recovery.ConcurrencyConflict);

        fixture.Db.ChangeTracker.Clear();
        conflict = await fixture.Db
            .InventoryTransferTombstoneConflicts
            .AsNoTracking()
            .SingleAsync(
                current => current.TransferId == fixture.TransferId);
        var outbox = await fixture.Db.SyncOutboxEntries
            .AsNoTracking()
            .SingleAsync(entry => entry.Id == fixture.OutboxId);
        Assert.Equal(
            InventoryTransferTombstoneConflictPolicy.UnresolvedStatus,
            conflict.Status);
        Assert.Null(conflict.RecoveredTransferId);
        Assert.Equal("Failed", outbox.Status);
        Assert.Null(outbox.AcknowledgedAtUtc);
        Assert.Equal(
            1,
            await fixture.Db.InventoryTransfers
                .IgnoreQueryFilters()
                .CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalItemRefresh_SelectedSavedLineDoesNotBecomePhantomDraft(
        bool itemDeleted)
    {
        await using var fixture = await CreateLoadedTransferFixtureAsync(
            $"georaeplan-transfer-item-refresh-{itemDeleted}");
        var viewModel = fixture.ViewModel;
        var db = fixture.Db;

        viewModel.SelectedLine = Assert.Single(viewModel.Lines);
        var inputNameBefore = viewModel.InputItemName;
        var inputSpecBefore = viewModel.InputSpec;
        var inputUnitBefore = viewModel.InputUnit;
        Assert.False(viewModel.HasPendingChanges);

        db.ChangeTracker.Clear();
        var item = await db.Items
            .IgnoreQueryFilters()
            .SingleAsync(current => current.Id == fixture.ItemId);
        item.NameOriginal = "Catalog name changed elsewhere";
        item.SpecificationOriginal = "Catalog specification changed elsewhere";
        item.Unit = "BOX";
        item.IsDeleted = itemDeleted;
        item.IsDirty = false;
        item.Revision++;
        item.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await viewModel.HandleInventoryStateChangedAsync();

        Assert.Equal(inputNameBefore, viewModel.InputItemName);
        Assert.Equal(inputSpecBefore, viewModel.InputSpec);
        Assert.Equal(inputUnitBefore, viewModel.InputUnit);
        Assert.False(viewModel.HasPendingChanges);
        Assert.False(viewModel.HasExternalTransferConflict);
        Assert.False(viewModel.IsExternalTransferUnavailable);
    }

    private static async Task<LoadedTransferFixture> CreateLoadedTransferFixtureAsync(
        string prefix)
    {
        var appRoot = new LocalAppRootScope(prefix);
        var db = CreateDbContext(appRoot.DbPath);
        try
        {
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var itemId = Guid.NewGuid();
            var transferId = Guid.NewGuid();
            db.Warehouses.AddRange(
                new LocalWarehouse
                {
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    Code = OfficeCodeCatalog.UsenetMainWarehouse,
                    Name = "Source warehouse",
                    IsActive = true,
                    IsDirty = false
                },
                new LocalWarehouse
                {
                    OfficeCode = OfficeCodeCatalog.Yeonsu,
                    Code = OfficeCodeCatalog.YeonsuMainWarehouse,
                    Name = "Target warehouse",
                    IsActive = true,
                    IsDirty = false
                });
            db.Items.Add(new LocalItem
            {
                Id = itemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Shared,
                NameOriginal = "External refresh transfer item",
                NameMatchKey = "EXTERNALREFRESHTRANSFERITEM",
                TrackingType = ItemTrackingTypes.NonStock,
                Unit = "EA",
                IsDirty = false
            });
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                Revision = 11,
                TransferNumber = "TR-EXTERNAL-REFRESH",
                TransferDate = new DateOnly(2026, 8, 1),
                FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
                ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
                Memo = "loaded baseline",
                TransferStatus = InventoryTransferStatusNormalizer.Pending,
                IsDirty = false,
                Lines =
                [
                    new LocalInventoryTransferLine
                    {
                        Id = Guid.NewGuid(),
                        TransferId = transferId,
                        ItemId = itemId,
                        ItemNameOriginal = "External refresh transfer item",
                        Unit = "EA",
                        Quantity = 1m,
                        ReceivedQuantity = 1m
                    }
                ]
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateGlobalAdminSession();
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var viewModel = new InventoryTransferViewModel(service, session);
            await viewModel.LoadAsync(new LocalInventoryTransfer { Id = transferId });

            return new LoadedTransferFixture(
                appRoot,
                db,
                viewModel,
                transferId,
                itemId);
        }
        catch
        {
            await db.DisposeAsync();
            appRoot.Dispose();
            throw;
        }
    }

    private static async Task<PersistedTombstoneConflictFixture>
        CreatePersistedTombstoneConflictFixtureAsync(
            string prefix,
            SessionState? runtimeSession = null,
            bool serverIsDeleted = true,
            string conflictResolution = "",
            bool serverRouteDiffers = false)
    {
        var appRoot = new LocalAppRootScope(prefix);
        var transferId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        var mutationId = Guid.NewGuid().ToString("N");
        var createdAtUtc = new DateTime(
            2026,
            8,
            1,
            1,
            0,
            0,
            DateTimeKind.Utc);
        var serverUpdatedAtUtc = createdAtUtc.AddMinutes(20);
        const long localRevision = 7;
        const long serverRevision = 12;
        var session = runtimeSession ?? CreateGlobalAdminSession();
        var businessDatabaseName =
            TenantScopeCatalog.GetDatabaseName(
                session.SelectedBusinessDatabaseName);
        var localDraft = new LocalInventoryTransfer
        {
            Id = transferId,
            Revision = localRevision,
            TransferNumber = "TR-LOCAL-DRAFT-007",
            TransferDate = new DateOnly(2026, 8, 1),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            Memo = "persisted local transfer draft",
            CreatedByUsername = "transfer-test-admin",
            LastSavedByUsername = "transfer-test-admin",
            LastSavedAtUtc = createdAtUtc.AddMinutes(5),
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            RequestedByUsername = "transfer-test-admin",
            RequestedAtUtc = createdAtUtc.AddMinutes(5),
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = createdAtUtc.AddMinutes(5),
            IsDeleted = false,
            IsDirty = true,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = lineId,
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "Persisted conflict item",
                    SpecificationOriginal = "shadow specification",
                    Unit = "EA",
                    Quantity = 3m,
                    ReceivedQuantity = 2m,
                    QuantityDifference = -1m,
                    Remark = "local draft line remark",
                    ReceiptRemark = "local draft receipt remark"
                }
            ]
        };
        var serverTombstone = new LocalInventoryTransfer
        {
            Id = transferId,
            Revision = serverRevision,
            TransferNumber = localDraft.TransferNumber,
            TransferDate = localDraft.TransferDate,
            FromWarehouseCode = serverRouteDiffers
                ? OfficeCodeCatalog.YeonsuMainWarehouse
                : localDraft.FromWarehouseCode,
            ToWarehouseCode = serverRouteDiffers
                ? OfficeCodeCatalog.UsenetMainWarehouse
                : localDraft.ToWarehouseCode,
            Memo = serverIsDeleted
                ? "server tombstone winner"
                : "server active restored winner",
            CreatedByUsername = localDraft.CreatedByUsername,
            LastSavedByUsername = "remote-user",
            LastSavedAtUtc = serverUpdatedAtUtc,
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            RequestedByUsername = localDraft.RequestedByUsername,
            RequestedAtUtc = localDraft.RequestedAtUtc,
            CreatedAtUtc = createdAtUtc,
            UpdatedAtUtc = serverUpdatedAtUtc,
            IsDeleted = serverIsDeleted,
            IsDirty = false
        };

        try
        {
            await using (var seedDb = CreateDbContext(appRoot.DbPath))
            {
                await seedDb.Database.EnsureDeletedAsync();
                await seedDb.Database.EnsureCreatedAsync();
                seedDb.Warehouses.AddRange(
                    new LocalWarehouse
                    {
                        OfficeCode = OfficeCodeCatalog.Usenet,
                        Code = OfficeCodeCatalog.UsenetMainWarehouse,
                        Name = "Source warehouse",
                        IsActive = true,
                        IsDirty = false
                    },
                    new LocalWarehouse
                    {
                        OfficeCode = OfficeCodeCatalog.Yeonsu,
                        Code = OfficeCodeCatalog.YeonsuMainWarehouse,
                        Name = "Target warehouse",
                        IsActive = true,
                        IsDirty = false
                    });
                seedDb.Items.Add(new LocalItem
                {
                    Id = itemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Shared,
                    NameOriginal = "Persisted conflict item",
                    NameMatchKey = "PERSISTEDCONFLICTITEM",
                    SpecificationOriginal = "shadow specification",
                    TrackingType = ItemTrackingTypes.NonStock,
                    Unit = "EA",
                    IsDirty = false
                });
                seedDb.InventoryTransfers.Add(serverTombstone);
                seedDb.SyncOutboxEntries.Add(new LocalSyncOutboxEntry
                {
                    Id = outboxId,
                    MutationId = mutationId,
                    DeviceId = "persisted-conflict-device",
                    EntityName = nameof(LocalInventoryTransfer),
                    EntityId = transferId,
                    ExpectedRevision = localRevision,
                    BusinessDatabaseName = businessDatabaseName,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                    Status = "Failed",
                    ErrorMessage =
                        $"{InventoryTransferTombstoneConflictPolicy.OutboxErrorPrefix} test",
                    PreparedAtUtc = createdAtUtc.AddMinutes(6)
                });
                seedDb.InventoryTransferTombstoneConflicts.Add(
                    new LocalInventoryTransferTombstoneConflict
                    {
                        TransferId = transferId,
                        BusinessDatabaseName = businessDatabaseName,
                        TenantCode = TenantScopeCatalog.UsenetGroup,
                        SourceOfficeCode = OfficeCodeCatalog.Usenet,
                        TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                        LocalSnapshotJson = JsonSerializer.Serialize(
                            LocalMappings.ToDto(localDraft)),
                        ServerTombstoneJson = JsonSerializer.Serialize(
                            LocalMappings.ToDto(serverTombstone)),
                        OutboxMutationIdsJson = JsonSerializer.Serialize(
                            new[] { mutationId }),
                        LocalRevision = localRevision,
                        ServerRevision = serverRevision,
                        ServerUpdatedAtUtc = serverUpdatedAtUtc,
                        Status =
                            InventoryTransferTombstoneConflictPolicy
                                .UnresolvedStatus,
                        Resolution = conflictResolution,
                        DetectedAtUtc = createdAtUtc.AddMinutes(21),
                        UpdatedAtUtc = createdAtUtc.AddMinutes(21)
                    });
                await seedDb.SaveChangesAsync();
            }

            var runtimeDb = CreateDbContext(appRoot.DbPath);
            try
            {
                var service = new LocalStateService(
                    runtimeDb,
                    new OfficeAccessService(),
                    new SyncRequestDispatcher(),
                    session);
                var viewModel = new InventoryTransferViewModel(
                    service,
                    session);
                await viewModel.LoadAsync(
                    new LocalInventoryTransfer { Id = transferId });

                return new PersistedTombstoneConflictFixture(
                    appRoot,
                    runtimeDb,
                    service,
                    session,
                    viewModel,
                    transferId,
                    outboxId,
                    serverRevision,
                    serverUpdatedAtUtc,
                    localDraft);
            }
            catch
            {
                await runtimeDb.DisposeAsync();
                throw;
            }
        }
        catch
        {
            appRoot.Dispose();
            throw;
        }
    }

    private static async Task<Guid> AddRemoteTransferAsync(
        LocalDbContext db,
        Guid itemId,
        string transferNumber)
    {
        var transferId = Guid.NewGuid();
        var now = DateTime.UtcNow.AddSeconds(1);
        db.ChangeTracker.Clear();
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            Revision = 20,
            TransferNumber = transferNumber,
            TransferDate = new DateOnly(2026, 8, 2),
            FromWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            ToWarehouseCode = OfficeCodeCatalog.YeonsuMainWarehouse,
            Memo = "remote transfer added while window is open",
            TransferStatus = InventoryTransferStatusNormalizer.Pending,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsDirty = false,
            Lines =
            [
                new LocalInventoryTransferLine
                {
                    Id = Guid.NewGuid(),
                    TransferId = transferId,
                    ItemId = itemId,
                    ItemNameOriginal = "External refresh transfer item",
                    Unit = "EA",
                    Quantity = 1m,
                    ReceivedQuantity = 1m
                }
            ]
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return transferId;
    }

    private static async Task<long> ApplyRemoteTransferChangeAsync(
        LocalDbContext db,
        Guid transferId,
        Action<LocalInventoryTransfer> change)
    {
        db.ChangeTracker.Clear();
        var transfer = await db.InventoryTransfers
            .IgnoreQueryFilters()
            .SingleAsync(current => current.Id == transferId);
        change(transfer);
        transfer.Revision++;
        transfer.IsDirty = false;
        transfer.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        var revision = transfer.Revision;
        db.ChangeTracker.Clear();
        return revision;
    }

    private static SessionState CreateUserSession(
        string officeCode,
        string scopeType)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = $"{officeCode.ToLowerInvariant()}-delivery-user",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode,
            ScopeType = scopeType,
            Permissions = [AppPermissionNames.DeliveryEdit]
        });
        return session;
    }

    private static SessionState CreateGlobalAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "transfer-test-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static SessionState CreateReadOnlyUserSession(
        string officeCode,
        string scopeType)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username =
                $"{officeCode.ToLowerInvariant()}-delivery-reader",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode,
            ScopeType = scopeType,
            Permissions = []
        });
        return session;
    }

    private static LocalDbContext CreateDbContext(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new LocalDbContext(options);
    }

    private sealed class LocalAppRootScope : IDisposable
    {
        private readonly string? _previousAppRoot;
        private readonly string _appRoot;

        public LocalAppRootScope(string prefix)
        {
            _previousAppRoot =
                Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
            _appRoot = Path.Combine(
                Path.GetTempPath(),
                $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_appRoot);
            DbPath = Path.Combine(_appRoot, "거래플랜-test.db");
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                _appRoot);
        }

        public string DbPath { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_APP_ROOT",
                _previousAppRoot);
            SqliteConnection.ClearAllPools();

            var fullTempRoot = Path.GetFullPath(Path.GetTempPath());
            var fullAppRoot = Path.GetFullPath(_appRoot);
            if (!fullAppRoot.StartsWith(
                    fullTempRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

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

    private sealed class LoadedTransferFixture(
        LocalAppRootScope appRoot,
        LocalDbContext db,
        InventoryTransferViewModel viewModel,
        Guid transferId,
        Guid itemId) : IAsyncDisposable
    {
        public LocalDbContext Db { get; } = db;
        public InventoryTransferViewModel ViewModel { get; } = viewModel;
        public Guid TransferId { get; } = transferId;
        public Guid ItemId { get; } = itemId;

        public async ValueTask DisposeAsync()
        {
            ViewModel.Dispose();
            await Db.DisposeAsync();
            appRoot.Dispose();
        }
    }

    private sealed class PersistedTombstoneConflictFixture(
        LocalAppRootScope appRoot,
        LocalDbContext db,
        LocalStateService service,
        SessionState session,
        InventoryTransferViewModel viewModel,
        Guid transferId,
        Guid outboxId,
        long serverRevision,
        DateTime serverUpdatedAtUtc,
        LocalInventoryTransfer localDraft) : IAsyncDisposable
    {
        public LocalDbContext Db { get; } = db;
        public LocalStateService Service { get; } = service;
        public SessionState Session { get; } = session;
        public InventoryTransferViewModel ViewModel { get; } = viewModel;
        public Guid TransferId { get; } = transferId;
        public Guid OutboxId { get; } = outboxId;
        public long ServerRevision { get; } = serverRevision;
        public DateTime ServerUpdatedAtUtc { get; } = serverUpdatedAtUtc;
        public LocalInventoryTransfer LocalDraft { get; } = localDraft;

        public async ValueTask DisposeAsync()
        {
            ViewModel.Dispose();
            await Db.DisposeAsync();
            appRoot.Dispose();
        }
    }
}
