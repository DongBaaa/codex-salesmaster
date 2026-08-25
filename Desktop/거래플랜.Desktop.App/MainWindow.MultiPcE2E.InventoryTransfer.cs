using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App;

public partial class MainWindow
{
    // This fixture deliberately remains 수령대기: the product rule is source-only outbound on save,
    // destination inbound only on receipt confirmation.  It must never invoke receipt/reject commands.
    private async Task RunMultiPcInventoryTransferRoleAAsync(MultiPcE2EContext context, ICollection<MultiPcE2EStep> steps)
    {
        var marker = BuildMultiPcInventoryTransferMarker(context.Contract.RunId);
        var plan = await SelectMultiPcInventoryTransferPlanAsync();
        context.OwnedInventoryTransferScope = plan.Scope;
        var before = await CaptureMultiPcInventoryTransferEvidenceAsync(
            plan.ItemId,
            plan.FromWarehouseCode,
            plan.ToWarehouseCode,
            marker);
        _ = await WaitForMultiPcInventoryTransferUiaGateAsync(
            context,
            "transfer-b-list-uia-ready.json",
            "before-create",
            TimeSpan.FromSeconds(120),
            gate =>
                gate.TargetProcessId == context.OtherProcessId &&
                gate.InventoryTransferId == Guid.Empty &&
                gate.ServerRevision == 0 &&
                gate.BeforeRowCount >= 0 &&
                gate.AfterRowCount == gate.BeforeRowCount);
        var vm = new InventoryTransferViewModel(_local, _session);
        await vm.LoadAsync();
        var window = ShowMultiPcInventoryTransferWindow(vm);
        Guid transferId;
        try
        {
            vm.FromWarehouseCode = plan.FromWarehouseCode;
            vm.ToWarehouseCode = plan.ToWarehouseCode;
            vm.ApplyInputItem(plan.Item);
            vm.InputQty = 1m;
            vm.InputRemark = marker;
            vm.AddLineCommand.Execute(null);
            RequireMultiPc(vm.Lines.Count == 1 && vm.Lines[0].ItemId == plan.ItemId, "Inventory transfer fixture did not retain its single selected line.");
            vm.Memo = marker + "|INITIAL";
            await vm.SaveTransferCommand.ExecuteAsync(null);
            transferId = vm.TransferId;
            context.OwnedInventoryTransferId = transferId;
            RequireMultiPc(transferId != Guid.Empty, $"Inventory transfer fixture UI save failed: {vm.StatusMessage}");
        }
        finally { window.DataContext = null; CloseWindowForSmoke(window); vm.Dispose(); }

        await SyncMultiPcAndRequireCleanAsync("A-inventory-transfer-create-sync");
        var created = await RequireMultiPcInventoryTransferAsync(
            transferId,
            expectedDeleted: false,
            marker,
            plan.Scope);
        var afterCreate = await CaptureMultiPcInventoryTransferEvidenceAsync(
            plan.ItemId,
            plan.FromWarehouseCode,
            plan.ToWarehouseCode,
            marker);
        RequireMultiPc(IsMultiPcInventoryTransferFixture(created, marker, plan) &&
            HasExpectedMultiPcPendingTransferAggregate(
                before.SourceQuantity,
                before.DestinationQuantity,
                afterCreate.SourceQuantity,
                afterCreate.DestinationQuantity,
                transferQuantity: 1m) &&
            afterCreate.SerialHash == before.SerialHash && afterCreate.HasExactSingleSourceTransferOut &&
            afterCreate.TransferMovementCount == 1 && afterCreate.TransferMovementDelta == -1m,
            "Pending inventory transfer did not produce exactly one source-only outbound effect.");
        await WriteMultiPcInventoryTransferSignalAsync(
            context,
            "transfer-a-created.json",
            transferId,
            created.Revision,
            created.Memo,
            plan.Scope,
            afterCreate);
        AddPassedStep(steps, "inventory-transfer-pending-create-source-outbound", "pending save: source=-1, destination unchanged, serial unchanged, transfer OUT=1");

        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "transfer-b-loaded.json",
            TimeSpan.FromSeconds(120),
            "B",
            signal => IsMultiPcInventoryTransferSignalForScope(signal, transferId, plan.Scope));
        var staleVm = new InventoryTransferViewModel(_local, _session);
        await staleVm.LoadAsync();
        await staleVm.OpenTransferAsync(transferId);
        var staleWindow = ShowMultiPcInventoryTransferWindow(staleVm);
        try
        {
            var pending = marker + "|A-PENDING";
            staleVm.Memo = pending;
            RequireMultiPc(staleVm.HasPendingChanges && staleVm.SelectedTransfer?.Id == transferId, "PC-A transfer stale draft/selection was not retained.");
            await WriteMultiPcInventoryTransferSignalAsync(
                context,
                "transfer-a-staged.json",
                transferId,
                created.Revision,
                pending,
                plan.Scope);
            AddPassedStep(steps, "inventory-transfer-stale-edit-staged", "pending transfer editor selection and draft retained");

            var bWritten = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "transfer-b-written.json",
                TimeSpan.FromSeconds(120),
                "B",
                signal => IsMultiPcInventoryTransferSignalForScope(signal, transferId, plan.Scope));
            await SyncMultiPcAndRequireCleanAsync("A-inventory-transfer-pull-winner");
            await WaitForMultiPcConditionAsync(
                () =>
                    !staleVm.IsBusy &&
                    staleVm.HasExternalTransferConflict &&
                    !staleVm.IsExternalTransferUnavailable &&
                    staleVm.SelectedTransfer?.Revision == bWritten.Revision,
                TimeSpan.FromSeconds(10),
                "PC-A transfer editor did not automatically observe the committed PC-B winner while preserving its draft.");
            var autoSaved = await staleVm.TryAutoSaveOnCloseAsync();
            var afterConflict = await RequireMultiPcInventoryTransferAsync(
                transferId,
                expectedDeleted: false,
                marker,
                plan.Scope);
            var afterConflictEvidence = await CaptureMultiPcInventoryTransferEvidenceAsync(
                plan.ItemId,
                plan.FromWarehouseCode,
                plan.ToWarehouseCode,
                marker);
            var outbox = await _local.GetSyncOutboxSummaryAsync(_session);
            RequireMultiPc(
                !autoSaved &&
                staleVm.HasPendingChanges &&
                staleVm.HasExternalTransferConflict &&
                !staleVm.IsExternalTransferUnavailable &&
                staleVm.SelectedTransfer?.Id == transferId &&
                staleVm.SelectedTransfer.Revision == bWritten.Revision &&
                string.Equals(staleVm.Memo, pending, StringComparison.Ordinal) &&
                afterConflict.Revision == bWritten.Revision &&
                string.Equals(afterConflict.Memo, marker + "|B-WINS", StringComparison.Ordinal) &&
                string.Equals(bWritten.Value, marker + "|B-WINS", StringComparison.Ordinal) &&
                !afterConflict.IsDirty &&
                outbox.PendingCount == 0 &&
                outbox.FailedCount == 0 &&
                afterConflictEvidence.Equals(afterCreate),
                "Transfer stale autosave conflict either lost its draft or duplicated pending outbound inventory effects.");
            await WriteMultiPcInventoryTransferSignalAsync(
                context,
                "transfer-a-conflict.json",
                transferId,
                afterConflict.Revision,
                pending,
                plan.Scope);
            AddPassedStep(steps, "inventory-transfer-stale-autosave-conflict", "automatic committed-pull refresh retained draft/old baseline; revision conflict; dirty/outbox=0; source OUT not duplicated");

            await staleVm.DiscardDraftAndReloadLatestTransferAsync();
            RequireMultiPc(staleVm.SelectedTransfer?.Id == transferId && staleVm.SelectedTransfer.Revision >= bWritten.Revision, "PC-A transfer explicit reload did not refresh revision baseline.");
            staleVm.Memo = marker + "|A-RETRY";
            await staleVm.SaveTransferCommand.ExecuteAsync(null);
            await SyncMultiPcAndRequireCleanAsync("A-inventory-transfer-retry-sync");
            var retried = await RequireMultiPcInventoryTransferAsync(
                transferId,
                expectedDeleted: false,
                marker,
                plan.Scope);
            bool RetryEditorConverged()
                =>
                    !staleVm.IsBusy &&
                    staleVm.SelectedTransfer?.Id == transferId &&
                    staleVm.SelectedTransfer.Revision == retried.Revision &&
                    !staleVm.SelectedTransfer.IsDirty &&
                    !staleVm.HasPendingChanges &&
                    !staleVm.HasExternalTransferConflict &&
                    !staleVm.IsExternalTransferUnavailable &&
                    string.Equals(
                        staleVm.FromWarehouseCode,
                        retried.FromWarehouseCode,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        staleVm.ToWarehouseCode,
                        retried.ToWarehouseCode,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        staleVm.Memo,
                        retried.Memo,
                        StringComparison.Ordinal);

            try
            {
                await WaitForMultiPcConditionAsync(
                    RetryEditorConverged,
                    TimeSpan.FromSeconds(15),
                    "PC-A transfer editor did not fully converge after the clean server-confirmed retry revision.");
            }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException(
                    BuildMultiPcInventoryTransferRetryMismatch(
                        staleVm,
                        retried),
                    ex);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
            var afterRetry = await CaptureMultiPcInventoryTransferEvidenceAsync(
                plan.ItemId,
                plan.FromWarehouseCode,
                plan.ToWarehouseCode,
                marker);
            RequireMultiPc(
                retried.Revision > bWritten.Revision &&
                string.Equals(retried.Memo, marker + "|A-RETRY", StringComparison.Ordinal) &&
                RetryEditorConverged() &&
                afterRetry.Equals(afterCreate),
                $"{BuildMultiPcInventoryTransferRetryMismatch(staleVm, retried)}; inventoryMatch={afterRetry.Equals(afterCreate)}");
            await WriteMultiPcInventoryTransferSignalAsync(
                context,
                "transfer-a-retried.json",
                transferId,
                retried.Revision,
                retried.Memo,
                plan.Scope);
            AddPassedStep(steps, "inventory-transfer-pull-reload-retry-save", "retry converged with exactly one source OUT and unchanged destination/serial");

            var bDeleted = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "transfer-b-deleted.json",
                TimeSpan.FromSeconds(120),
                "B",
                signal => IsMultiPcInventoryTransferSignalForScope(signal, transferId, plan.Scope));
            var staleDelete = await PushMultiPcStaleInventoryTransferAsync(retried, true, context.Contract.RunId);
            RequireMultiPc(
                IsMultiPcInventoryTransferIdempotentDeleteNoOp(
                    staleDelete,
                    transferId,
                    bDeleted.Revision),
                "Runner-owned server did not preserve the existing inventory-transfer tombstone for an idempotent stale delete no-op.");
            AddPassedStep(
                steps,
                "inventory-transfer-server-idempotent-stale-delete-no-op",
                "accepted=1; conflicts=0; accepted revision equals the existing PC-B tombstone revision");
            await SyncMultiPcAndRequireCleanAsync("A-inventory-transfer-pull-delete");
            await WaitForMultiPcConditionAsync(
                () =>
                    !staleVm.IsBusy &&
                    ((staleVm.TransferId == Guid.Empty &&
                      staleVm.SelectedTransfer is null) ||
                     staleVm.IsExternalTransferUnavailable ||
                     staleVm.HasExternalTransferConflict),
                TimeSpan.FromSeconds(10),
                "PC-A clean transfer editor did not automatically reset after the committed PC-B tombstone.");
            RequireMultiPc(
                staleVm.TransferId == Guid.Empty &&
                staleVm.SelectedTransfer is null &&
                !staleVm.HasPendingChanges &&
                !staleVm.HasExternalTransferConflict &&
                !staleVm.IsExternalTransferUnavailable,
                $"PC-A tombstone refresh left an invalid editor state: transferId={staleVm.TransferId:D}; selected={staleVm.SelectedTransfer?.Id.ToString("D") ?? "none"}; pending={staleVm.HasPendingChanges}; conflict={staleVm.HasExternalTransferConflict}; unavailable={staleVm.IsExternalTransferUnavailable}; status={staleVm.StatusMessage}");
            var deleted = await RequireMultiPcInventoryTransferAsync(
                transferId,
                expectedDeleted: true,
                marker,
                plan.Scope);
            var restored = await CaptureMultiPcInventoryTransferEvidenceAsync(
                plan.ItemId,
                plan.FromWarehouseCode,
                plan.ToWarehouseCode,
                marker);
            RequireMultiPc(
                deleted.Revision == bDeleted.Revision &&
                deleted.Revision > retried.Revision &&
                deleted.IsDeleted &&
                !deleted.IsDirty &&
                !staleVm.HasExternalTransferConflict &&
                !staleVm.IsExternalTransferUnavailable &&
                string.Equals(deleted.Memo, retried.Memo, StringComparison.Ordinal) &&
                restored.SourceQuantity == before.SourceQuantity &&
                restored.DestinationQuantity == before.DestinationQuantity &&
                restored.SerialHash == before.SerialHash &&
                restored.LayerHash == afterCreate.LayerHash &&
                restored.MovementHash == afterCreate.MovementHash &&
                restored.TransferMovementCount == 1 &&
                restored.TransferMovementDelta == -1m,
                "Inventory-transfer idempotent stale delete no-op did not preserve the PC-B tombstone revision/content and canonical inventory state.");
            await WriteMultiPcInventoryTransferSignalAsync(
                context,
                "transfer-a-delete-observed.json",
                transferId,
                deleted.Revision,
                "deleted",
                plan.Scope);
            AddPassedStep(
                steps,
                "inventory-transfer-idempotent-delete-propagation",
                "PC-B tombstone revision/content/deleted state preserved after pull; canonical stock restored; dirty=false");
            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "transfer-b-purged.json",
                TimeSpan.FromSeconds(120),
                "B",
                signal => IsMultiPcInventoryTransferSignalForScope(signal, transferId, plan.Scope));
            await SyncMultiPcAndRequireCleanAsync("A-inventory-transfer-pull-purge");
            RequireMultiPc(
                await GetMultiPcInventoryTransferRawAsync(transferId) is null &&
                (await CaptureMultiPcInventoryTransferEvidenceAsync(
                    plan.ItemId,
                    plan.FromWarehouseCode,
                    plan.ToWarehouseCode,
                    marker)).Equals(before),
                "PC-A transfer purge did not leave exact inventory baseline.");
            await WriteMultiPcInventoryTransferSignalAsync(
                context,
                "transfer-a-clean.json",
                transferId,
                deleted.Revision,
                "purged",
                plan.Scope);
            AddPassedStep(steps, "inventory-transfer-fixture-purge-no-residue", "exact marker pending transfer absent; inventory baseline restored");
        }
        finally { staleWindow.DataContext = null; CloseWindowForSmoke(staleWindow); staleVm.Dispose(); }
    }

    private static string BuildMultiPcInventoryTransferRetryMismatch(
        InventoryTransferViewModel viewModel,
        LocalInventoryTransfer retried)
    {
        var mismatches = new List<string>();

        AddMismatch(viewModel.TransferId == retried.Id, "transfer.id");
        AddMismatch(
            string.Equals(
                viewModel.TransferNumber,
                retried.TransferNumber ?? string.Empty,
                StringComparison.Ordinal),
            "transfer.number");
        AddMismatch(viewModel.TransferDate == retried.TransferDate, "transfer.date");
        AddMismatch(
            string.Equals(
                viewModel.FromWarehouseCode,
                retried.FromWarehouseCode ?? string.Empty,
                StringComparison.Ordinal),
            "transfer.from");
        AddMismatch(
            string.Equals(
                viewModel.ToWarehouseCode,
                retried.ToWarehouseCode ?? string.Empty,
                StringComparison.Ordinal),
            "transfer.to");
        AddMismatch(
            string.Equals(
                viewModel.Memo,
                retried.Memo ?? string.Empty,
                StringComparison.Ordinal),
            "transfer.memo");
        AddMismatch(
            string.Equals(
                viewModel.TransferStatus,
                retried.TransferStatus ?? string.Empty,
                StringComparison.Ordinal),
            "transfer.status");
        AddMismatch(
            string.Equals(
                viewModel.ReceiveMemo,
                retried.ReceiveMemo ?? string.Empty,
                StringComparison.Ordinal),
            "transfer.receiveMemo");
        AddMismatch(
            string.Equals(
                viewModel.RejectReason,
                retried.RejectReason ?? string.Empty,
                StringComparison.Ordinal),
            "transfer.rejectReason");

        var editorLines = viewModel.Lines.ToList();
        var retriedLines = retried.Lines
            .Where(line => !line.IsDeleted)
            .ToList();
        AddMismatch(editorLines.Count == retriedLines.Count, "lines.count");
        for (var index = 0;
             index < Math.Min(editorLines.Count, retriedLines.Count);
             index++)
        {
            var editorLine = editorLines[index];
            var retriedLine = retriedLines[index];
            var prefix = $"lines[{index}]";
            AddMismatch(editorLine.Id == retriedLine.Id, $"{prefix}.id");
            AddMismatch(editorLine.ItemId == retriedLine.ItemId, $"{prefix}.itemId");
            AddMismatch(
                string.Equals(
                    editorLine.ItemName,
                    retriedLine.ItemNameOriginal ?? string.Empty,
                    StringComparison.Ordinal),
                $"{prefix}.name");
            AddMismatch(
                string.Equals(
                    editorLine.Specification,
                    retriedLine.SpecificationOriginal ?? string.Empty,
                    StringComparison.Ordinal),
                $"{prefix}.spec");
            AddMismatch(
                string.Equals(
                    editorLine.Unit,
                    retriedLine.Unit ?? string.Empty,
                    StringComparison.Ordinal),
                $"{prefix}.unit");
            AddMismatch(editorLine.Quantity == retriedLine.Quantity, $"{prefix}.quantity");
            AddMismatch(
                editorLine.ReceivedQuantity ==
                (retriedLine.ReceivedQuantity ?? retriedLine.Quantity),
                $"{prefix}.receivedQuantity");
            AddMismatch(
                string.Equals(
                    editorLine.Remark,
                    retriedLine.Remark ?? string.Empty,
                    StringComparison.Ordinal),
                $"{prefix}.remark");
            AddMismatch(
                string.Equals(
                    editorLine.ReceiptRemark,
                    retriedLine.ReceiptRemark ?? string.Empty,
                    StringComparison.Ordinal),
                $"{prefix}.receiptRemark");
        }

        var selectedLine = viewModel.SelectedLine;
        if (selectedLine is null)
        {
            var hasMeaningfulInput =
                viewModel.SelectedInputItem is not null ||
                !string.IsNullOrWhiteSpace(viewModel.InputItemName) ||
                !string.IsNullOrWhiteSpace(viewModel.InputSpec) ||
                !string.IsNullOrWhiteSpace(viewModel.InputUnit) ||
                !string.IsNullOrWhiteSpace(viewModel.InputRemark) ||
                !string.IsNullOrWhiteSpace(viewModel.InputReceiptRemark) ||
                viewModel.InputQty != 1m ||
                viewModel.InputReceivedQty != 1m;
            AddMismatch(!hasMeaningfulInput, "lineEditor.orphanDraft");
        }
        else
        {
            var resolvedItemId =
                viewModel.SelectedInputItem?.Id ??
                selectedLine.ItemId;
            var normalizedReceivedQuantity =
                viewModel.InputReceivedQty <= 0m
                    ? viewModel.InputQty
                    : viewModel.InputReceivedQty;
            AddMismatch(resolvedItemId == selectedLine.ItemId, "lineEditor.itemId");
            AddMismatch(
                string.Equals(
                    (viewModel.InputItemName ?? string.Empty).Trim(),
                    selectedLine.ItemName ?? string.Empty,
                    StringComparison.Ordinal),
                "lineEditor.name");
            AddMismatch(
                string.Equals(
                    (viewModel.InputSpec ?? string.Empty).Trim(),
                    selectedLine.Specification ?? string.Empty,
                    StringComparison.Ordinal),
                "lineEditor.spec");
            AddMismatch(
                string.Equals(
                    (viewModel.InputUnit ?? string.Empty).Trim(),
                    selectedLine.Unit ?? string.Empty,
                    StringComparison.Ordinal),
                "lineEditor.unit");
            AddMismatch(viewModel.InputQty == selectedLine.Quantity, "lineEditor.quantity");
            AddMismatch(
                normalizedReceivedQuantity == selectedLine.ReceivedQuantity,
                "lineEditor.receivedQuantity");
            AddMismatch(
                string.Equals(
                    (viewModel.InputRemark ?? string.Empty).Trim(),
                    selectedLine.Remark ?? string.Empty,
                    StringComparison.Ordinal),
                "lineEditor.remark");
            AddMismatch(
                string.Equals(
                    (viewModel.InputReceiptRemark ?? string.Empty).Trim(),
                    selectedLine.ReceiptRemark ?? string.Empty,
                    StringComparison.Ordinal),
                "lineEditor.receiptRemark");
        }

        if (mismatches.Count == 0 && viewModel.HasPendingChanges)
            mismatches.Add("baseline-only");

        return
            $"Transfer retry state mismatch: dbRevision={retried.Revision}; " +
            $"editorRevision={viewModel.SelectedTransfer?.Revision}; " +
            $"editorDirty={viewModel.SelectedTransfer?.IsDirty}; " +
            $"pending={viewModel.HasPendingChanges}; " +
            $"conflict={viewModel.HasExternalTransferConflict}; " +
            $"unavailable={viewModel.IsExternalTransferUnavailable}; " +
            $"mismatches={string.Join(',', mismatches)}; " +
            $"status={viewModel.StatusMessage}";

        void AddMismatch(bool matches, string fieldName)
        {
            if (!matches)
                mismatches.Add(fieldName);
        }
    }

    private async Task RunMultiPcInventoryTransferRoleBAsync(MultiPcE2EContext context, ICollection<MultiPcE2EStep> steps)
    {
        var marker = BuildMultiPcInventoryTransferMarker(context.Contract.RunId);
        var vm = new InventoryTransferViewModel(_local, _session);
        await vm.LoadAsync();
        var beforeRowCount = vm.Transfers.Count;
        var window = ShowMultiPcInventoryTransferWindow(vm);
        var windowHandle = new WindowInteropHelper(window).Handle.ToInt64();
        var transferId = Guid.Empty;
        LocalInventoryTransfer created = null!;
        MultiPcInventoryTransferPlan plan = null!;
        MultiPcInventoryEvidence afterCreate = null!;
        var realtimeRevisionMonitorActive = false;
        try
        {
            StartMultiPcRealtimeRevisionObservation();
            realtimeRevisionMonitorActive = true;
            RequireMultiPc(window.IsVisible && windowHandle > 0, "PC-B inventory-transfer list window did not become a real visible HWND.");
            await WriteMultiPcInventoryTransferListSignalAsync(
                context,
                "transfer-b-list-ready.json",
                Guid.Empty,
                revision: 0,
                beforeRowCount,
                beforeRowCount,
                windowHandle,
                "visible-list-before-create");
            var beforeUiaGate = await WaitForMultiPcInventoryTransferUiaGateAsync(
                context,
                "transfer-b-list-uia-ready.json",
                "before-create",
                TimeSpan.FromSeconds(120),
                gate =>
                    gate.TargetProcessId == Environment.ProcessId &&
                    gate.WindowNativeHandle == windowHandle &&
                    gate.InventoryTransferId == Guid.Empty &&
                    gate.ServerRevision == 0 &&
                    gate.BeforeRowCount == beforeRowCount &&
                    gate.AfterRowCount == beforeRowCount);

            var createdSignal = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "transfer-a-created.json",
                TimeSpan.FromSeconds(120),
                "A",
                signal =>
                    signal.InventoryTransferId != Guid.Empty &&
                    signal.Revision > 0 &&
                    signal.CapturedAtUtc > beforeUiaGate.CapturedAtUtc);
            transferId = createdSignal.InventoryTransferId;
            await WaitForMultiPcConditionAsync(
                () =>
                    !vm.IsBusy &&
                    _lastCentralRefreshUtc > createdSignal.CapturedAtUtc.UtcDateTime &&
                    vm.Transfers.Count == beforeRowCount + 1 &&
                    vm.Transfers.Any(transfer =>
                        transfer.Id == transferId &&
                        transfer.Revision == createdSignal.Revision),
                TimeSpan.FromSeconds(60),
                "PC-B already-open inventory-transfer list did not update through the realtime revision monitor after the committed server revision.");
            await WriteMultiPcInventoryTransferListSignalAsync(
                context,
                "transfer-b-list-vm-updated.json",
                transferId,
                createdSignal.Revision,
                beforeRowCount,
                vm.Transfers.Count,
                windowHandle,
                "in-process-vm-list-updated");
            _ = await WaitForMultiPcInventoryTransferUiaGateAsync(
                context,
                "transfer-b-list-uia-updated.json",
                "after-create",
                TimeSpan.FromSeconds(70),
                gate =>
                    gate.TargetProcessId == Environment.ProcessId &&
                    gate.WindowNativeHandle == windowHandle &&
                    gate.InventoryTransferId == transferId &&
                    gate.ServerRevision == createdSignal.Revision &&
                    gate.BeforeRowCount == beforeRowCount &&
                    gate.AfterRowCount == beforeRowCount + 1);
            StopMultiPcRealtimeRevisionObservation();
            realtimeRevisionMonitorActive = false;

            created = await RequireMultiPcInventoryTransferAsync(transferId, expectedDeleted: false, marker);
            plan = await PlanFromMultiPcInventoryTransferAsync(created);
            RequireMultiPc(
                IsMultiPcInventoryTransferSignalForScope(createdSignal, transferId, plan.Scope),
                "PC-B refused inventory-transfer coordination whose item/warehouse/tenant scope differs from the pulled fixture.");
            created = await RequireMultiPcInventoryTransferAsync(
                transferId,
                expectedDeleted: false,
                marker,
                plan.Scope);
            afterCreate = await CaptureMultiPcInventoryTransferEvidenceAsync(
                plan.ItemId,
                plan.FromWarehouseCode,
                plan.ToWarehouseCode,
                marker);
            // Pull synchronizes the server-canonical warehouse stock snapshot and
            // transfer rows. Movement/layer rows are device-local derived
            // projections and are rebuilt only by a local inventory mutation.
            RequireMultiPc(
                IsMultiPcInventoryTransferFixture(created, marker, plan) &&
                createdSignal.SourceQuantity.HasValue &&
                createdSignal.DestinationQuantity.HasValue &&
                afterCreate.SourceQuantity == createdSignal.SourceQuantity.Value &&
                afterCreate.DestinationQuantity == createdSignal.DestinationQuantity.Value &&
                afterCreate.TransferMovementCount == 0 &&
                afterCreate.TransferMovementDelta == 0m &&
                !afterCreate.HasExactSingleSourceTransferOut,
                "PC-B refused unverified pending inventory-transfer fixture.");
            context.OwnedInventoryTransferId = transferId;
            context.OwnedInventoryTransferScope = plan.Scope;
            await vm.OpenTransferAsync(transferId);
            await WriteMultiPcInventoryTransferSignalAsync(
                context,
                "transfer-b-loaded.json",
                transferId,
                created.Revision,
                created.Memo,
                plan.Scope);
            AddPassedStep(steps, "inventory-transfer-cross-client-pull", "InventoryTransferViewModel selected pending source-only transfer");
            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
                context,
                "transfer-a-staged.json",
                TimeSpan.FromSeconds(120),
                "A",
                signal => IsMultiPcInventoryTransferSignalForScope(signal, transferId, plan.Scope));
            vm.Memo = marker + "|B-WINS";
            await vm.SaveTransferCommand.ExecuteAsync(null);
            RequireMultiPc(vm.TransferId == transferId, $"PC-B inventory transfer UI save failed: {vm.StatusMessage}");
        }
        finally
        {
            if (realtimeRevisionMonitorActive)
                StopMultiPcRealtimeRevisionObservation();
            window.DataContext = null;
            CloseWindowForSmoke(window);
            vm.Dispose();
        }
        await SyncMultiPcAndRequireCleanAsync("B-inventory-transfer-write-sync");
        var written = await RequireMultiPcInventoryTransferAsync(
            transferId,
            expectedDeleted: false,
            marker,
            plan.Scope);
        var afterWrite = await CaptureMultiPcInventoryTransferEvidenceAsync(
            plan.ItemId,
            plan.FromWarehouseCode,
            plan.ToWarehouseCode,
            marker);
        RequireMultiPc(
            written.Revision > created.Revision &&
            string.Equals(written.Memo, marker + "|B-WINS", StringComparison.Ordinal) &&
            afterWrite.SourceQuantity == afterCreate.SourceQuantity &&
            afterWrite.DestinationQuantity == afterCreate.DestinationQuantity &&
            afterWrite.SerialHash == afterCreate.SerialHash &&
            afterWrite.HasExactSingleSourceTransferOut &&
            afterWrite.TransferMovementCount == 1 &&
            afterWrite.TransferMovementDelta == -1m,
            "PC-B pending memo winner save changed canonical stock or failed to build exactly one local source OUT projection.");
        await WriteMultiPcInventoryTransferSignalAsync(
            context,
            "transfer-b-written.json",
            transferId,
            written.Revision,
            written.Memo,
            plan.Scope);
        AddPassedStep(steps, "inventory-transfer-winner-save-and-sync", "PC-B pending memo winner kept single source OUT");
        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "transfer-a-retried.json",
            TimeSpan.FromSeconds(120),
            "A",
            signal => IsMultiPcInventoryTransferSignalForScope(signal, transferId, plan.Scope));
        await SyncMultiPcAndRequireCleanAsync("B-inventory-transfer-pull-retry");
        var latest = await RequireMultiPcInventoryTransferAsync(
            transferId,
            expectedDeleted: false,
            marker,
            plan.Scope);
        var afterRetryPull = await CaptureMultiPcInventoryTransferEvidenceAsync(
            plan.ItemId,
            plan.FromWarehouseCode,
            plan.ToWarehouseCode,
            marker);
        RequireMultiPc(
            IsMultiPcInventoryTransferFixture(latest, marker, plan) &&
            latest.Revision > written.Revision &&
            string.Equals(latest.Memo, marker + "|A-RETRY", StringComparison.Ordinal) &&
            afterRetryPull.Equals(afterWrite),
            "PC-B transfer delete refused: marker/scope/status/line/retry-value verification failed.");
        var delete = await _local.DeleteInventoryTransferAsync(transferId, _session, latest.Revision);
        RequireMultiPc(delete.Success, $"PC-B pending inventory transfer delete failed: {delete.Message}");
        await SyncMultiPcAndRequireCleanAsync("B-inventory-transfer-delete-sync");
        var deleted = await RequireMultiPcInventoryTransferAsync(
            transferId,
            expectedDeleted: true,
            marker,
            plan.Scope);
        var restored = await CaptureMultiPcInventoryTransferEvidenceAsync(
            plan.ItemId,
            plan.FromWarehouseCode,
            plan.ToWarehouseCode,
            marker);
        RequireMultiPc(
            deleted.Revision > latest.Revision &&
            restored.SourceQuantity == afterCreate.SourceQuantity + 1m &&
            restored.DestinationQuantity == afterCreate.DestinationQuantity &&
            restored.LayerHash == afterCreate.LayerHash &&
            restored.SerialHash == afterCreate.SerialHash &&
            restored.MovementHash == afterCreate.MovementHash &&
            restored.TransferMovementCount == 0 &&
            restored.TransferMovementDelta == 0m,
            "PC-B pending transfer delete did not restore the source/layer/movement baseline.");
        await WriteMultiPcInventoryTransferSignalAsync(
            context,
            "transfer-b-deleted.json",
            transferId,
            deleted.Revision,
            "deleted",
            plan.Scope);
        AddPassedStep(steps, "inventory-transfer-delete-and-sync", "pending delete restored source and removed transfer OUT residue");
        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(
            context,
            "transfer-a-delete-observed.json",
            TimeSpan.FromSeconds(120),
            "A",
            signal => IsMultiPcInventoryTransferSignalForScope(signal, transferId, plan.Scope));
        var deletedForPurge = await RequireMultiPcInventoryTransferAsync(
            transferId,
            expectedDeleted: true,
            marker,
            plan.Scope);
        var purge = await _api.PurgeRecycleBinAsync([new RecycleBinMutationTargetDto { EntityId = transferId, Kind = "inventory-transfer", ExpectedRevision = deletedForPurge.Revision }]);
        RequireMultiPc(purge is not null && purge.RequestedCount == 1 && purge.SucceededCount == 1, "Server inventory-transfer fixture purge failed.");
        await SyncMultiPcAndRequireCleanAsync("B-inventory-transfer-pull-purge");
        RequireMultiPc(
            await GetMultiPcInventoryTransferRawAsync(transferId) is null &&
            (await CaptureMultiPcInventoryTransferEvidenceAsync(
                plan.ItemId,
                plan.FromWarehouseCode,
                plan.ToWarehouseCode,
                marker)).Equals(restored),
            "PC-B inventory-transfer fixture or canonical inventory baseline remains inconsistent after purge.");
        await WriteMultiPcInventoryTransferSignalAsync(
            context,
            "transfer-b-purged.json",
            transferId,
            deletedForPurge.Revision,
            "purged",
            plan.Scope);
        AddPassedStep(steps, "server-inventory-transfer-fixture-purge", "marker-bound pending transfer purge succeeded without inventory residue");
    }

    private async Task WriteMultiPcInventoryTransferListSignalAsync(
        MultiPcE2EContext context,
        string fileName,
        Guid transferId,
        long revision,
        int beforeRowCount,
        int afterRowCount,
        long windowNativeHandle,
        string value)
    {
        await WriteMultiPcJsonAtomicAsync(
            Path.Combine(context.RunRoot, fileName),
            new MultiPcSignal
            {
                RunId = context.Contract.RunId,
                Nonce = context.Contract.Nonce,
                Role = context.Role,
                ProcessId = Environment.ProcessId,
                InventoryTransferId = transferId,
                Revision = revision,
                BeforeRowCount = beforeRowCount,
                AfterRowCount = afterRowCount,
                WindowNativeHandle = windowNativeHandle,
                RealtimeRevisionMonitorActive = _realtimeRevisionCts is not null &&
                    _realtimeRevisionTask is { IsCompleted: false },
                PassiveRefreshCompletedAtUtc = _lastCentralRefreshUtc == DateTime.MinValue
                    ? null
                    : new DateTimeOffset(
                        DateTime.SpecifyKind(_lastCentralRefreshUtc, DateTimeKind.Utc)),
                Value = value,
                CapturedAtUtc = DateTimeOffset.UtcNow
            });
    }

    private void StartMultiPcRealtimeRevisionObservation()
    {
        RequireMultiPc(
            _isClosingOrClosed &&
            _realtimeRevisionCts is null &&
            _realtimeRevisionTask is null &&
            _realtimeRevisionDrainTask.IsCompletedSuccessfully &&
            _runtimeSyncDrainTask.IsCompletedSuccessfully &&
            _windowCommandDrainTask.IsCompletedSuccessfully &&
            _vm.IsShutdownBackgroundWorkCompleted,
            $"PC-B realtime revision monitor did not start from the isolated stopped-runtime boundary. " +
            $"closing={_isClosingOrClosed}; monitorCtsNull={_realtimeRevisionCts is null}; " +
            $"monitorTaskNull={_realtimeRevisionTask is null}; monitorDrain={_realtimeRevisionDrainTask.Status}; " +
            $"runtimeSyncDrain={_runtimeSyncDrainTask.Status}; windowCommandDrain={_windowCommandDrainTask.Status}; " +
            $"viewModelBackgroundCompleted={_vm.IsShutdownBackgroundWorkCompleted}");

        _vm.ResumePendingBackgroundWorkAfterShutdownCanceled();
        _windowBackgroundWork.Resume();
        _windowBackgroundWorkCts.Dispose();
        _windowBackgroundWorkCts = new CancellationTokenSource();
        RestoreApplicationWindowsAfterCanceledShutdown();
        _isClosingOrClosed = false;
        StartRealtimeRevisionMonitor();
        RequireMultiPc(
            _realtimeRevisionCts is not null &&
            _realtimeRevisionTask is { IsCompleted: false },
            "PC-B realtime revision monitor was not active before the inventory-transfer list readiness signal.");
    }

    private void StopMultiPcRealtimeRevisionObservation()
    {
        StopRealtimeRevisionMonitor();
        _windowBackgroundWork.BeginShutdown();
        try
        {
            _windowBackgroundWorkCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The full shutdown-resume path owns disposal; keep the fixture stop idempotent.
        }
        _vm.CancelPendingBackgroundWorkForShutdown();
        _isClosingOrClosed = true;
    }

    private static async Task<MultiPcUiaGate> WaitForMultiPcInventoryTransferUiaGateAsync(
        MultiPcE2EContext context,
        string fileName,
        string expectedPhase,
        TimeSpan timeout,
        Func<MultiPcUiaGate, bool> predicate)
    {
        var path = Path.Combine(context.RunRoot, fileName);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        Exception? lastReadError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path))
                {
                    await using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);
                    var gate = await System.Text.Json.JsonSerializer.DeserializeAsync<MultiPcUiaGate>(
                        stream,
                        MultiPcJsonOptions);
                    if (gate is not null &&
                        string.Equals(gate.RunId, context.Contract.RunId, StringComparison.Ordinal) &&
                        string.Equals(gate.Nonce, context.Contract.Nonce, StringComparison.Ordinal) &&
                        string.Equals(gate.Role, "RUNNER", StringComparison.Ordinal) &&
                        gate.ProcessId == context.Contract.RunnerProcessId &&
                        string.Equals(gate.Phase, expectedPhase, StringComparison.Ordinal) &&
                        string.Equals(gate.TargetRole, "B", StringComparison.Ordinal) &&
                        string.Equals(gate.WindowAutomationId, "InventoryTransferWindow", StringComparison.Ordinal) &&
                        string.Equals(gate.ListAutomationId, "TransferListGrid", StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(gate.WindowRuntimeId) &&
                        !string.IsNullOrWhiteSpace(gate.ListRuntimeId) &&
                        gate.CapturedAtUtc >= context.Contract.CreatedAtUtc.AddSeconds(-5) &&
                        gate.CapturedAtUtc <= context.Contract.ExpiresAtUtc &&
                        predicate(gate))
                    {
                        return gate;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                lastReadError = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        throw new TimeoutException(
            lastReadError is null
                ? $"Timed out waiting for out-of-process UIA gate: {fileName}."
                : $"Timed out waiting for out-of-process UIA gate: {fileName}; lastError={lastReadError.Message}");
    }

    private InventoryTransferWindow ShowMultiPcInventoryTransferWindow(InventoryTransferViewModel vm)
    { var window = new InventoryTransferWindow(vm) { Owner = this, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner }; window.Show(); return window; }

    private async Task<MultiPcInventoryTransferPlan> SelectMultiPcInventoryTransferPlanAsync()
    {
        var writable = _local.GetWritableOfficeCodesForSession(_session);
        var warehouses = await _local.GetWarehousesForInventoryTransferAsync(_session);
        var sources = warehouses.Where(w => writable.Contains(w.OfficeCode, StringComparer.OrdinalIgnoreCase)).ToList();
        var scopedItems = (await _local.GetItemsForInventoryTransferAsync(_session))
            .Where(item => !item.IsDeleted && ItemOperationalPolicy.SupportsInventory(item.TrackingType))
            .ToDictionary(item => item.Id);
        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(
            _session.TenantCode,
            TenantScopeCatalog.GetTenantCodeForOffice(_session.OfficeCode));
        await using var db = new LocalDbContext();
        foreach (var source in sources)
        {
            var destination = warehouses.FirstOrDefault(w => !string.Equals(w.Code, source.Code, StringComparison.OrdinalIgnoreCase));
            if (destination is null) continue;
            var candidateIds = await db.ItemWarehouseStocks
                .AsNoTracking()
                .Where(stock => stock.WarehouseCode == source.Code && stock.Quantity >= 1m)
                .OrderBy(stock => stock.ItemId)
                .Select(stock => stock.ItemId)
                .ToListAsync();
            var eligibleLayerItemIds =
                await GetMultiPcInventoryTransferUnitLayerEligibleItemIdsAsync(
                    db,
                    source.Code);
            foreach (var candidateId in candidateIds)
            {
                if (!scopedItems.TryGetValue(candidateId, out var candidate) ||
                    !string.IsNullOrWhiteSpace(candidate.SerialNumber) ||
                    !eligibleLayerItemIds.Contains(candidateId))
                {
                    continue;
                }

                var scope = new MultiPcInventoryTransferScope(
                    candidate.Id,
                    source.Code,
                    destination.Code,
                    tenantCode);
                return new MultiPcInventoryTransferPlan(candidate, scope);
            }
        }
        throw new InvalidOperationException(
            "Accessible non-serial item with source stock >= 1, a single layer able to supply qty=1, and two distinct warehouses is required; no master/stock fixture was created.");
    }

    internal static async Task<HashSet<Guid>>
        GetMultiPcInventoryTransferUnitLayerEligibleItemIdsAsync(
            LocalDbContext db,
            string warehouseCode,
            CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (string.IsNullOrWhiteSpace(warehouseCode))
            return [];

        var normalizedWarehouseCode = warehouseCode.Trim();
        var itemIdsWithUnitLayer = await db.StockLayers
            .AsNoTracking()
            .Where(layer =>
                layer.ItemId.HasValue &&
                layer.WarehouseCode == normalizedWarehouseCode &&
                layer.RemainingQuantity >= 1m)
            .Select(layer => layer.ItemId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var itemIdsWithSubUnitPositiveLayer = await db.StockLayers
            .AsNoTracking()
            .Where(layer =>
                layer.ItemId.HasValue &&
                layer.WarehouseCode == normalizedWarehouseCode &&
                layer.RemainingQuantity > 0m &&
                layer.RemainingQuantity < 1m)
            .Select(layer => layer.ItemId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var eligibleItemIds = itemIdsWithUnitLayer.ToHashSet();
        eligibleItemIds.ExceptWith(itemIdsWithSubUnitPositiveLayer);
        return eligibleItemIds;
    }

    private async Task<MultiPcInventoryTransferPlan> PlanFromMultiPcInventoryTransferAsync(LocalInventoryTransfer transfer)
    {
        var line = transfer.Lines.SingleOrDefault(line => !line.IsDeleted && line.ItemId.HasValue && line.Quantity == 1m) ?? throw new InvalidOperationException("Pending transfer fixture must retain exactly one qty=1 line.");
        var item = (await _local.GetItemsForInventoryTransferAsync(_session))
            .SingleOrDefault(item => item.Id == line.ItemId!.Value && !item.IsDeleted)
            ?? throw new InvalidOperationException("Transfer fixture source item is absent from the authenticated tenant scope.");
        var scope = new MultiPcInventoryTransferScope(
            item.Id,
            transfer.FromWarehouseCode,
            transfer.ToWarehouseCode,
            TenantScopeCatalog.NormalizeTenantCodeOrDefault(
                _session.TenantCode,
                TenantScopeCatalog.GetTenantCodeForOffice(_session.OfficeCode)));
        RequireMultiPc(
            await IsMultiPcInventoryTransferScopeAsync(transfer, scope),
            "Transfer fixture item/warehouse scope is not writable by the authenticated tenant session.");
        return new MultiPcInventoryTransferPlan(item, scope);
    }

    private async Task<LocalInventoryTransfer> RequireMultiPcInventoryTransferAsync(
        Guid id,
        bool expectedDeleted,
        string marker,
        MultiPcInventoryTransferScope? expectedScope = null)
    {
        var transfer = await GetMultiPcInventoryTransferRawAsync(id);
        RequireMultiPc(
            transfer is not null &&
            transfer.IsDeleted == expectedDeleted &&
            IsMultiPcInventoryTransferFixture(transfer!, marker, expectedScope) &&
            await IsMultiPcInventoryTransferScopeAsync(transfer!, expectedScope) &&
            !transfer.IsDirty,
            "Inventory transfer marker/scope/status/line/dependency or deletion verification failed.");
        return transfer!;
    }

    private static bool IsMultiPcInventoryTransferFixture(
        LocalInventoryTransfer transfer,
        string marker,
        MultiPcInventoryTransferPlan? plan)
        => IsMultiPcInventoryTransferFixture(transfer, marker, plan?.Scope);

    private static bool IsMultiPcInventoryTransferFixture(
        LocalInventoryTransfer transfer,
        string marker,
        MultiPcInventoryTransferScope? expectedScope)
    {
        var activeLines = transfer.Lines.Where(line => !line.IsDeleted).ToList();
        if (activeLines.Count != 1)
            return false;

        var line = activeLines[0];
        return string.Equals(transfer.TransferStatus, "수령대기", StringComparison.Ordinal) &&
               transfer.Memo.StartsWith(marker + "|", StringComparison.Ordinal) &&
               string.IsNullOrWhiteSpace(transfer.ReceivedByUsername) &&
               transfer.ReceivedAtUtc is null &&
               string.IsNullOrWhiteSpace(transfer.ReceiveMemo) &&
               string.IsNullOrWhiteSpace(transfer.ReceiveEvidencePath) &&
               string.IsNullOrWhiteSpace(transfer.RejectedByUsername) &&
               transfer.RejectedAtUtc is null &&
               string.IsNullOrWhiteSpace(transfer.RejectReason) &&
               line.ItemId.HasValue &&
               line.Quantity == 1m &&
               HasNeutralMultiPcPendingReceiptValues(line) &&
               (expectedScope is null ||
                (line.ItemId == expectedScope.ItemId &&
                 string.Equals(
                     transfer.FromWarehouseCode,
                     expectedScope.FromWarehouseCode,
                     StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(
                     transfer.ToWarehouseCode,
                     expectedScope.ToWarehouseCode,
                     StringComparison.OrdinalIgnoreCase)));
    }

    internal static bool HasNeutralMultiPcPendingReceiptValues(
        LocalInventoryTransferLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var receivedQuantity = line.ReceivedQuantity ?? line.Quantity;
        var quantityDifference =
            line.QuantityDifference ?? (receivedQuantity - line.Quantity);
        return receivedQuantity == line.Quantity &&
               quantityDifference == 0m &&
               string.IsNullOrWhiteSpace(line.ReceiptRemark);
    }

    internal static bool HasExpectedMultiPcPendingTransferAggregate(
        decimal sourceQuantityBefore,
        decimal destinationQuantityBefore,
        decimal sourceQuantityAfter,
        decimal destinationQuantityAfter,
        decimal transferQuantity)
        => transferQuantity > 0m &&
           sourceQuantityAfter == sourceQuantityBefore - transferQuantity &&
           destinationQuantityAfter == destinationQuantityBefore;

    private async Task<bool> IsMultiPcInventoryTransferScopeAsync(
        LocalInventoryTransfer transfer,
        MultiPcInventoryTransferScope? expectedScope)
    {
        var activeLine = transfer.Lines.SingleOrDefault(line => !line.IsDeleted);
        if (activeLine?.ItemId is not Guid itemId || itemId == Guid.Empty)
            return false;

        var tenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(
            _session.TenantCode,
            TenantScopeCatalog.GetTenantCodeForOffice(_session.OfficeCode));
        var warehouses = await _local.GetWarehousesForInventoryTransferAsync(_session);
        var source = warehouses.SingleOrDefault(warehouse =>
            string.Equals(warehouse.Code, transfer.FromWarehouseCode, StringComparison.OrdinalIgnoreCase));
        var destination = warehouses.SingleOrDefault(warehouse =>
            string.Equals(warehouse.Code, transfer.ToWarehouseCode, StringComparison.OrdinalIgnoreCase));
        var writableOfficeCodes = _local.GetWritableOfficeCodesForSession(_session);
        var scopedItem = (await _local.GetItemsForInventoryTransferAsync(_session))
            .SingleOrDefault(item => item.Id == itemId && !item.IsDeleted);

        return source is not null &&
               destination is not null &&
               scopedItem is not null &&
               !string.Equals(source.Code, destination.Code, StringComparison.OrdinalIgnoreCase) &&
               writableOfficeCodes.Contains(source.OfficeCode, StringComparer.OrdinalIgnoreCase) &&
               string.Equals(
                   TenantScopeCatalog.GetTenantCodeForOffice(source.OfficeCode),
                   tenantCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   TenantScopeCatalog.GetTenantCodeForOffice(destination.OfficeCode),
                   tenantCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                       scopedItem.TenantCode,
                       scopedItem.OfficeCode,
                       tenantCode),
                   tenantCode,
                   StringComparison.OrdinalIgnoreCase) &&
               (expectedScope is null ||
                (expectedScope.ItemId == itemId &&
                 string.Equals(expectedScope.TenantCode, tenantCode, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(
                     expectedScope.FromWarehouseCode,
                     source.Code,
                     StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(
                     expectedScope.ToWarehouseCode,
                     destination.Code,
                     StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<LocalInventoryTransfer?> GetMultiPcInventoryTransferRawAsync(Guid id)
    { await using var db = new LocalDbContext(); return await db.InventoryTransfers.IgnoreQueryFilters().Include(transfer => transfer.Lines).AsNoTracking().FirstOrDefaultAsync(transfer => transfer.Id == id); }

    private async Task<MultiPcInventoryEvidence> CaptureMultiPcInventoryTransferEvidenceAsync(
        Guid itemId,
        string from,
        string to,
        string marker)
    {
        await using var db = new LocalDbContext();
        var source = await db.ItemWarehouseStocks.AsNoTracking().Where(stock => stock.ItemId == itemId && stock.WarehouseCode == from).Select(stock => (decimal?)stock.Quantity).FirstOrDefaultAsync() ?? 0m;
        var destination = await db.ItemWarehouseStocks.AsNoTracking().Where(stock => stock.ItemId == itemId && stock.WarehouseCode == to).Select(stock => (decimal?)stock.Quantity).FirstOrDefaultAsync() ?? 0m;
        var layerRows = await db.StockLayers
            .AsNoTracking()
            .Where(layer =>
                layer.ItemId == itemId &&
                (layer.WarehouseCode == from || layer.WarehouseCode == to))
            .Select(layer => new
            {
                layer.WarehouseCode,
                layer.SourceInvoiceId,
                layer.SourceInvoiceLineId,
                layer.ReceiptDate,
                layer.UnitCost,
                layer.OriginalQuantity,
                layer.RemainingQuantity,
                layer.IsNegativePlaceholder
            })
            .ToListAsync();
        var serialRows = await db.SerialLedgers
            .AsNoTracking()
            .Where(serial => serial.ItemId == itemId)
            .Select(serial => new
            {
                serial.SerialNumber,
                serial.WarehouseCode,
                serial.Status,
                serial.SourcePurchaseInvoiceId,
                serial.SourceSalesInvoiceId,
                serial.LastInvoiceId,
                serial.LastMovementType,
                serial.Memo
            })
            .ToListAsync();
        var movements = await db.InventoryMovements
            .AsNoTracking()
            .Where(movement =>
                movement.ItemId == itemId &&
                movement.IsActive &&
                movement.Note.Contains(marker))
            .Select(movement => new
            {
                movement.WarehouseCode,
                movement.MovementType,
                movement.QuantityDelta,
                movement.UnitCost,
                movement.Amount,
                movement.OccurredDate,
                movement.IsSettledCost,
                movement.IsActive,
                movement.Note
            })
            .ToListAsync();

        var layerHash = string.Join(
            "|",
            layerRows
                .Select(layer => string.Join(
                    "\u001f",
                    layer.WarehouseCode,
                    FormatMultiPcGuid(layer.SourceInvoiceId),
                    FormatMultiPcGuid(layer.SourceInvoiceLineId),
                    layer.ReceiptDate.ToString("O", CultureInfo.InvariantCulture),
                    FormatMultiPcDecimal(layer.UnitCost),
                    FormatMultiPcDecimal(layer.OriginalQuantity),
                    FormatMultiPcDecimal(layer.RemainingQuantity),
                    layer.IsNegativePlaceholder ? "1" : "0"))
                .OrderBy(value => value, StringComparer.Ordinal));
        var serialHash = string.Join(
            "|",
            serialRows
                .Select(serial => string.Join(
                    "\u001f",
                    serial.SerialNumber,
                    serial.WarehouseCode,
                    serial.Status,
                    FormatMultiPcGuid(serial.SourcePurchaseInvoiceId),
                    FormatMultiPcGuid(serial.SourceSalesInvoiceId),
                    FormatMultiPcGuid(serial.LastInvoiceId),
                    serial.LastMovementType,
                    serial.Memo))
                .OrderBy(value => value, StringComparer.Ordinal));
        var movementHash = string.Join(
            "|",
            movements
                .Select(movement => string.Join(
                    "\u001f",
                    movement.WarehouseCode,
                    movement.MovementType,
                    FormatMultiPcDecimal(movement.QuantityDelta),
                    FormatMultiPcDecimal(movement.UnitCost),
                    FormatMultiPcDecimal(movement.Amount),
                    movement.OccurredDate.ToString("O", CultureInfo.InvariantCulture),
                    movement.IsSettledCost ? "1" : "0",
                    movement.IsActive ? "1" : "0",
                    movement.Note))
                .OrderBy(value => value, StringComparer.Ordinal));
        var hasExactSingleSourceTransferOut =
            movements.Count == 1 &&
            string.Equals(movements[0].WarehouseCode, from, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(movements[0].MovementType, "TransferOutManual", StringComparison.Ordinal) &&
            movements[0].QuantityDelta == -1m;

        return new MultiPcInventoryEvidence(
            source,
            destination,
            layerHash,
            serialHash,
            movementHash,
            movements.Count,
            movements.Sum(movement => movement.QuantityDelta),
            hasExactSingleSourceTransferOut);
    }

    private async Task<SyncPushResult?> PushMultiPcStaleInventoryTransferAsync(LocalInventoryTransfer transfer, bool deleted, string runId)
    { var dto = LocalMappings.ToDto(transfer); dto.IsDeleted = deleted; dto.ExpectedRevision = transfer.Revision; dto.Revision = transfer.Revision; dto.MutationId = $"multipc-{runId}-transfer-stale-{Guid.NewGuid():N}"; dto.MutationCreatedAtUtc = DateTime.UtcNow; return await _api.PushAsync(new SyncPushRequest { DeviceId = (await _local.GetSettingAsync("Sync.DeviceId") ?? string.Empty).Trim(), InventoryTransfers = [dto] }); }

    private static bool IsMultiPcInventoryTransferIdempotentDeleteNoOp(
        SyncPushResult? push,
        Guid id,
        long tombstoneRevision)
        => push is { AcceptedCount: 1, ConflictCount: 0 } &&
           push.Conflicts.Count == 0 &&
           push.AcceptedRevisions.Count == 1 &&
           push.AcceptedRevisions.Count(accepted =>
               string.Equals(
                   accepted.EntityName,
                   "InventoryTransfer",
                   StringComparison.OrdinalIgnoreCase) &&
               accepted.EntityId == id &&
               accepted.Revision == tombstoneRevision) == 1;

    private static bool IsMultiPcInventoryTransferSignalForScope(
        MultiPcSignal signal,
        Guid transferId,
        MultiPcInventoryTransferScope scope)
        => signal.InventoryTransferId == transferId &&
           signal.ItemId == scope.ItemId &&
           string.Equals(signal.TenantCode, scope.TenantCode, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               signal.FromWarehouseCode,
               scope.FromWarehouseCode,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               signal.ToWarehouseCode,
               scope.ToWarehouseCode,
               StringComparison.OrdinalIgnoreCase);

    private static string FormatMultiPcGuid(Guid? value)
        => value?.ToString("D") ?? string.Empty;

    private static string FormatMultiPcDecimal(decimal value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static string BuildMultiPcInventoryTransferMarker(string runId) => $"CODEX-MULTIPC-{new string(runId.Where(char.IsLetterOrDigit).Take(16).ToArray())}-TRANSFER";

    private async Task<string> TryCleanupFailedMultiPcInventoryTransferFixtureAsync(MultiPcE2EContext context, Guid transferId)
    {
        await _sync.TrySyncAsync(); var transfer = await GetMultiPcInventoryTransferRawAsync(transferId); if (transfer is null) return "inventory transfer already absent";
        var marker = BuildMultiPcInventoryTransferMarker(context.Contract.RunId);
        var scope = context.OwnedInventoryTransferScope ??
                    throw new InvalidOperationException("Failure cleanup refused because the owned inventory-transfer scope was not recorded.");
        RequireMultiPc(
            IsMultiPcInventoryTransferFixture(transfer, marker, scope) &&
            await IsMultiPcInventoryTransferScopeAsync(transfer, scope),
            "Failure cleanup refused because transfer marker/status/line/session scope did not match.");
        if (!transfer.IsDeleted) { var delete = await _local.DeleteInventoryTransferAsync(transferId, _session, transfer.Revision); RequireMultiPc(delete.Success, "Failure cleanup inventory transfer delete failed."); await SyncMultiPcAndRequireCleanAsync("failure-inventory-transfer-delete"); }
        var deleted = await RequireMultiPcInventoryTransferAsync(transferId, true, marker, scope); var purge = await _api.PurgeRecycleBinAsync([new RecycleBinMutationTargetDto { EntityId = transferId, Kind = "inventory-transfer", ExpectedRevision = deleted.Revision }]); RequireMultiPc(purge is not null && purge.SucceededCount == 1, "Failure cleanup inventory transfer purge failed."); await SyncMultiPcAndRequireCleanAsync("failure-inventory-transfer-purge"); RequireMultiPc(await GetMultiPcInventoryTransferRawAsync(transferId) is null, "Failure cleanup inventory transfer remains."); return "pending transfer exact marker purged; source inventory restored";
    }

    private sealed record MultiPcInventoryTransferPlan(
        LocalItem Item,
        MultiPcInventoryTransferScope Scope)
    {
        public Guid ItemId => Scope.ItemId;
        public string FromWarehouseCode => Scope.FromWarehouseCode;
        public string ToWarehouseCode => Scope.ToWarehouseCode;
    }

    private sealed record MultiPcInventoryTransferScope(
        Guid ItemId,
        string FromWarehouseCode,
        string ToWarehouseCode,
        string TenantCode);

    private sealed record MultiPcInventoryEvidence(
        decimal SourceQuantity,
        decimal DestinationQuantity,
        string LayerHash,
        string SerialHash,
        string MovementHash,
        int TransferMovementCount,
        decimal TransferMovementDelta,
        bool HasExactSingleSourceTransferOut);
}
