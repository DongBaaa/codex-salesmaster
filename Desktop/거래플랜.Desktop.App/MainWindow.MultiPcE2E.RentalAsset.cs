using System.Text.Json;
using System.Windows;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App;

public partial class MainWindow
{
    // Profile-free asset fixture. It has no customer, catalog item, billing-profile, or financial link.
    private async Task RunMultiPcRentalAssetRoleAAsync(MultiPcE2EContext context, ICollection<MultiPcE2EStep> steps)
    {
        var marker = BuildMultiPcRentalAssetMarker(context.Contract.RunId);
        var vm = new RentalAssetViewModel(_rental, _local, _rentalDocuments, _invoicePrintService, _session)
        {
            SuppressExplicitSaveConflictDialog = true
        };
        await vm.LoadAsync();
        var window = ShowMultiPcRentalAssetWindow(vm);
        Guid assetId;
        try
        {
            vm.EditManagementId = marker;
            vm.EditManagementNumber = marker;
            vm.EditMachineNumber = marker;
            vm.EditOfficeCode = _session.OfficeCode;
            vm.EditCurrentLocation = "MULTIPC-ISOLATED";
            vm.EditInstallLocation = string.Empty;
            vm.EditItemId = null;
            vm.EditItemName = string.Empty;
            vm.EditAssetStatus = "창고";
            vm.EditBillingEligibilityStatus = "청구제외";
            vm.EditBillingExclusionReason = marker;
            vm.EditNotes = $"{marker}|INITIAL";
            assetId = vm.EditId;
            context.OwnedRentalAssetId = assetId;
            await vm.SaveCommand.ExecuteAsync(null);
            RequireMultiPc(vm.SelectedRow?.Source.Id == assetId, $"Rental asset fixture UI save failed: {vm.StatusMessage}");
        }
        finally { window.DataContext = null; CloseWindowForSmoke(window); }

        await SyncMultiPcAndRequireCleanAsync("A-rental-asset-create-sync");
        var created = await RequireMultiPcRentalAssetAsync(assetId, marker);
        await WriteMultiPcRentalAssetSignalAsync(context, "asset-a-created.json", assetId, created.Revision, created.Notes);
        AddPassedStep(steps, "rental-asset-create-and-sync", "profile-free RentalAssetViewModel fixture saved; dirty=false");

        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "asset-b-loaded.json", TimeSpan.FromSeconds(120), "B", signal => signal.RentalAssetId == assetId);
        var staleVm = new RentalAssetViewModel(_rental, _local, _rentalDocuments, _invoicePrintService, _session)
        {
            SuppressExplicitSaveConflictDialog = true
        };
        await staleVm.LoadAndSelectAssetAsync(assetId);
        var stale = staleVm.SelectedRow?.Source ?? throw new InvalidOperationException("PC-A could not select stale rental asset fixture.");
        var staleWindow = ShowMultiPcRentalAssetWindow(staleVm);
        try
        {
            var pending = $"{marker}|A-PENDING";
            staleVm.EditNotes = pending;
            staleVm.CancelPendingEditAutoSave();
            RequireMultiPc(staleVm.HasPendingChanges && staleVm.SelectedRow?.Source.Id == assetId, "PC-A rental asset stale edit was not retained.");
            await WriteMultiPcRentalAssetSignalAsync(context, "asset-a-staged.json", assetId, stale.Revision, pending);
            AddPassedStep(steps, "rental-asset-stale-edit-staged", "selected asset and pending editor content retained");

            var bWritten = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "asset-b-written.json", TimeSpan.FromSeconds(120), "B", signal => signal.RentalAssetId == assetId);
            await SyncMultiPcAndRequireCleanAsync("A-rental-asset-pull-winner");
            var autoSaved = await staleVm.TryAutoSaveOnCloseAsync();
            var afterConflict = await RequireMultiPcRentalAssetAsync(assetId, marker);
            var outbox = await _local.GetSyncOutboxSummaryAsync(_session);
            RequireMultiPc(
                !autoSaved &&
                staleVm.HasPendingChanges &&
                staleVm.SelectedRow?.Source.Id == assetId &&
                string.Equals(staleVm.EditNotes, pending, StringComparison.Ordinal) &&
                afterConflict.Revision == bWritten.Revision &&
                string.Equals(afterConflict.Notes, $"{marker}|B-WINS", StringComparison.Ordinal) &&
                string.Equals(bWritten.Value, $"{marker}|B-WINS", StringComparison.Ordinal) &&
                !afterConflict.IsDirty &&
                outbox.PendingCount == 0 &&
                outbox.FailedCount == 0,
                "Rental asset stale automatic save did not preserve draft/selection while leaving clean server state.");
            await WriteMultiPcRentalAssetSignalAsync(context, "asset-a-conflict.json", assetId, afterConflict.Revision, pending);
            AddPassedStep(steps, "rental-asset-stale-autosave-conflict", "TryAutoSaveOnClose failed on revision conflict; selection/draft retained; dirty/outbox=0");

            await staleVm.LoadAndSelectAssetAsync(assetId).WaitAsync(TimeSpan.FromSeconds(30));
            RequireMultiPc(
                staleVm.SelectedRow?.Source.Id == assetId &&
                staleVm.SelectedRow.Source.Revision >= bWritten.Revision &&
                staleVm.EditExpectedRevision == staleVm.SelectedRow.Source.Revision,
                "PC-A rental asset reload did not refresh the selected row and editor revision together.");
            staleVm.EditNotes = $"{marker}|A-RETRY";
            await staleVm.SaveCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(30));
            await staleVm.WaitForEditAutoSaveQuiescenceAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));
            await SyncMultiPcAndRequireCleanAsync("A-rental-asset-retry-sync");
            await staleVm.WaitForEditAutoSaveQuiescenceAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));
            var retried = await RequireMultiPcRentalAssetAsync(assetId, marker);
            var retryOutbox = await _local.GetSyncOutboxSummaryAsync(_session);
            await Task.Delay(TimeSpan.FromSeconds(1));
            await staleVm.WaitForEditAutoSaveQuiescenceAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));
            var stabilizedRetry = await RequireMultiPcRentalAssetAsync(assetId, marker);
            var stabilizedRetryOutbox = await _local.GetSyncOutboxSummaryAsync(_session);
            RequireMultiPc(
                retried.Revision > bWritten.Revision &&
                string.Equals(retried.Notes, $"{marker}|A-RETRY", StringComparison.Ordinal) &&
                !retried.IsDirty &&
                retryOutbox.PendingCount == 0 &&
                retryOutbox.FailedCount == 0 &&
                stabilizedRetry.Revision == retried.Revision &&
                string.Equals(stabilizedRetry.Notes, retried.Notes, StringComparison.Ordinal) &&
                !stabilizedRetry.IsDirty &&
                stabilizedRetryOutbox.PendingCount == 0 &&
                stabilizedRetryOutbox.FailedCount == 0 &&
                !staleVm.IsEditAutoSaveOwnershipActive &&
                !staleVm.HasPendingChanges &&
                staleVm.SelectedRow?.Source.Id == assetId &&
                staleVm.SelectedRow.Source.Revision == stabilizedRetry.Revision &&
                staleVm.EditExpectedRevision == stabilizedRetry.Revision,
                "Rental asset pull/reload retry did not reach a quiescent exact marker-bound fixture.");
            await WriteMultiPcRentalAssetSignalAsync(context, "asset-a-retried.json", assetId, retried.Revision, retried.Notes);
            AddPassedStep(
                steps,
                "rental-asset-pull-reload-retry-save",
                "explicit ViewModel retry reached stable owner/pending/revision/dirty/outbox quiescence");

            var bDeleted = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "asset-b-deleted.json", TimeSpan.FromSeconds(120), "B", signal => signal.RentalAssetId == assetId);
            var staleDelete = await PushMultiPcStaleRentalAssetAsync(retried, retried.Notes, true, context.Contract.RunId);
            RequireMultiPc(
                IsMultiPcRentalAssetIdempotentDeleteNoOp(
                    staleDelete,
                    assetId,
                    bDeleted.Revision),
                "Runner-owned server did not preserve the existing rental asset tombstone for an idempotent stale delete no-op.");
            AddPassedStep(
                steps,
                "rental-asset-server-idempotent-stale-delete-no-op",
                "accepted=1; conflicts=0; accepted revision equals the existing PC-B tombstone revision");
            await SyncMultiPcAndRequireCleanAsync("A-rental-asset-pull-delete");
            var deleted = await RequireMultiPcRentalAssetAsync(assetId, marker, expectedDeleted: true);
            RequireMultiPc(
                deleted.Revision == bDeleted.Revision &&
                deleted.Revision > retried.Revision &&
                deleted.IsDeleted &&
                !deleted.IsDirty &&
                string.Equals(deleted.ManagementId, retried.ManagementId, StringComparison.Ordinal) &&
                string.Equals(deleted.ManagementNumber, retried.ManagementNumber, StringComparison.Ordinal) &&
                string.Equals(deleted.MachineNumber, retried.MachineNumber, StringComparison.Ordinal) &&
                string.Equals(deleted.CurrentLocation, retried.CurrentLocation, StringComparison.Ordinal) &&
                string.Equals(deleted.Notes, retried.Notes, StringComparison.Ordinal),
                "Rental asset idempotent stale delete no-op did not preserve the PC-B tombstone revision, content, and deletion state.");
            await WriteMultiPcRentalAssetSignalAsync(context, "asset-a-delete-observed.json", assetId, deleted.Revision, "deleted");
            AddPassedStep(
                steps,
                "rental-asset-idempotent-delete-propagation",
                "PC-B tombstone revision/content/deleted state preserved after pull; dirty=false");
            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "asset-b-purged.json", TimeSpan.FromSeconds(120), "B", signal => signal.RentalAssetId == assetId);
            await SyncMultiPcAndRequireCleanAsync("A-rental-asset-pull-purge");
            await staleVm.WaitForEditAutoSaveQuiescenceAsync()
                .WaitAsync(TimeSpan.FromSeconds(30));
            RequireMultiPc(
                staleVm.SelectedRow is null &&
                staleVm.EditId != assetId &&
                !staleVm.HasPendingChanges &&
                !staleVm.IsEditAutoSaveOwnershipActive,
                "PC-A rental asset editor did not safely reset after purge convergence.");
            RequireMultiPc(await _rental.GetAssetRowAsync(assetId, _session) is null, "PC-A rental asset purge cleanup did not converge.");
            await WriteMultiPcRentalAssetSignalAsync(context, "asset-a-clean.json", assetId, deleted.Revision, "purged");
            AddPassedStep(steps, "rental-asset-fixture-purge-no-residue", "profile-free asset absent; no customer/item/billing dependency created");
        }
        finally { staleWindow.DataContext = null; CloseWindowForSmoke(staleWindow); }
    }

    private async Task RunMultiPcRentalAssetRoleBAsync(MultiPcE2EContext context, ICollection<MultiPcE2EStep> steps)
    {
        var created = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "asset-a-created.json", TimeSpan.FromSeconds(120), "A", signal => signal.RentalAssetId != Guid.Empty);
        var assetId = created.RentalAssetId;
        await SyncMultiPcAndRequireCleanAsync("B-rental-asset-pull-created");
        var marker = BuildMultiPcRentalAssetMarker(context.Contract.RunId);
        var loaded = await RequireMultiPcRentalAssetAsync(assetId, marker);
        context.OwnedRentalAssetId = assetId;
        var vm = new RentalAssetViewModel(_rental, _local, _rentalDocuments, _invoicePrintService, _session)
        {
            SuppressExplicitSaveConflictDialog = true
        };
        await vm.LoadAndSelectAssetAsync(assetId);
        var window = ShowMultiPcRentalAssetWindow(vm);
        try
        {
            await WriteMultiPcRentalAssetSignalAsync(context, "asset-b-loaded.json", assetId, loaded.Revision, loaded.Notes);
            AddPassedStep(steps, "rental-asset-cross-client-pull", "RentalAssetViewModel selected profile-free pulled asset");
            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "asset-a-staged.json", TimeSpan.FromSeconds(120), "A", signal => signal.RentalAssetId == assetId);
            vm.EditNotes = $"{marker}|B-WINS";
            await vm.SaveCommand.ExecuteAsync(null);
            RequireMultiPc(vm.SelectedRow?.Source.Id == assetId, $"PC-B rental asset UI save failed: {vm.StatusMessage}");
        }
        finally { window.DataContext = null; CloseWindowForSmoke(window); }
        await SyncMultiPcAndRequireCleanAsync("B-rental-asset-write-sync");
        var written = await RequireMultiPcRentalAssetAsync(assetId, marker);
        RequireMultiPc(
            written.Revision > loaded.Revision &&
            string.Equals(written.Notes, $"{marker}|B-WINS", StringComparison.Ordinal),
            "PC-B rental asset winner value was not acknowledged exactly.");
        await WriteMultiPcRentalAssetSignalAsync(context, "asset-b-written.json", assetId, written.Revision, written.Notes);
        AddPassedStep(steps, "rental-asset-winner-save-and-sync", "PC-B ViewModel winner saved; dirty=false");
        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "asset-a-retried.json", TimeSpan.FromSeconds(120), "A", signal => signal.RentalAssetId == assetId);
        await SyncMultiPcAndRequireCleanAsync("B-rental-asset-pull-retry");
        var latest = await RequireMultiPcRentalAssetAsync(assetId, marker);
        RequireMultiPc(
            latest.Revision > written.Revision &&
            string.Equals(latest.Notes, $"{marker}|A-RETRY", StringComparison.Ordinal),
            "PC-B did not pull the exact rental asset retry value before delete.");
        var delete = await _rental.DeleteAssetAsync(assetId, _session, latest.Revision);
        RequireMultiPc(delete.Success, $"PC-B rental asset delete failed: {delete.Message}");
        await SyncMultiPcAndRequireCleanAsync("B-rental-asset-delete-sync");
        var deleted = await RequireMultiPcRentalAssetAsync(assetId, marker, expectedDeleted: true);
        await WriteMultiPcRentalAssetSignalAsync(context, "asset-b-deleted.json", assetId, deleted.Revision, "deleted");
        AddPassedStep(steps, "rental-asset-delete-and-sync", "profile-free asset deleted and synced");
        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "asset-a-delete-observed.json", TimeSpan.FromSeconds(120), "A", signal => signal.RentalAssetId == assetId);
        var deletedForPurge = await RequireMultiPcRentalAssetAsync(assetId, marker, expectedDeleted: true);
        var purge = await _api.PurgeRecycleBinAsync([new RecycleBinMutationTargetDto { EntityId = assetId, Kind = "rental-asset", ExpectedRevision = deletedForPurge.Revision }]);
        RequireMultiPc(purge is not null && purge.RequestedCount == 1 && purge.SucceededCount == 1, "Server rental asset fixture purge failed.");
        await SyncMultiPcAndRequireCleanAsync("B-rental-asset-pull-purge");
        RequireMultiPc(await _rental.GetAssetRowAsync(assetId, _session) is null, "PC-B rental asset fixture remains after purge.");
        await WriteMultiPcRentalAssetSignalAsync(context, "asset-b-purged.json", assetId, deletedForPurge.Revision, "purged");
        AddPassedStep(steps, "server-rental-asset-fixture-purge", "marker-bound rental-asset purge succeeded");
    }

    private RentalAssetWindow ShowMultiPcRentalAssetWindow(RentalAssetViewModel vm)
    {
        var window = new RentalAssetWindow(vm) { Owner = this, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Show(); return window;
    }

    private async Task<LocalRentalAsset> RequireMultiPcRentalAssetAsync(
        Guid id,
        string marker,
        bool expectedDeleted = false)
    {
        await using var db = new LocalDbContext();
        var asset = await db.RentalAssets.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(asset => asset.Id == id);
        RequireMultiPc(asset is not null && asset.IsDeleted == expectedDeleted, "Rental asset fixture deletion state did not match the expected state.");
        var verifiedAsset = asset!;
        RequireMultiPc(
            IsMultiPcRentalAssetFixture(verifiedAsset, marker) &&
            await HasNoMultiPcRentalAssetDependenciesAsync(id, db) &&
            !verifiedAsset.IsDirty,
            "Rental asset fixture marker/scope/dependency/dirty verification failed.");
        return verifiedAsset;
    }

    private async Task<SyncPushResult?> PushMultiPcStaleRentalAssetAsync(LocalRentalAsset asset, string notes, bool deleted, string runId)
    {
        var dto = LocalMappings.ToDto(asset);
        dto.Notes = notes; dto.IsDeleted = deleted; dto.ExpectedRevision = asset.Revision; dto.Revision = asset.Revision;
        dto.MutationId = $"multipc-{runId}-asset-stale-{Guid.NewGuid():N}"; dto.MutationCreatedAtUtc = DateTime.UtcNow;
        return await _api.PushAsync(new SyncPushRequest { DeviceId = (await _local.GetSettingAsync("Sync.DeviceId") ?? string.Empty).Trim(), RentalAssets = [dto] });
    }

    private static bool IsMultiPcRentalAssetConflict(SyncPushResult? push, Guid id)
        => push is { AcceptedCount: 0, ConflictCount: 1 } &&
           push.Conflicts.Count(conflict =>
               string.Equals(conflict.EntityName, "RentalAsset", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(conflict.EntityId, id.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
               conflict.Reason.StartsWith("Expected revision mismatch.", StringComparison.Ordinal)) == 1;

    private static bool IsMultiPcRentalAssetIdempotentDeleteNoOp(
        SyncPushResult? push,
        Guid id,
        long tombstoneRevision)
        => push is { AcceptedCount: 1, ConflictCount: 0 } &&
           push.Conflicts.Count == 0 &&
           push.AcceptedRevisions.Count == 1 &&
           push.AcceptedRevisions.Count(accepted =>
               string.Equals(accepted.EntityName, "RentalAsset", StringComparison.OrdinalIgnoreCase) &&
               accepted.EntityId == id &&
               accepted.Revision == tombstoneRevision) == 1;

    private static string BuildMultiPcRentalAssetMarker(string runId)
        => $"CODEX-MULTIPC-{new string(runId.Where(char.IsLetterOrDigit).Take(16).ToArray())}-ASSET";

    private bool IsMultiPcRentalAssetFixture(LocalRentalAsset asset, string marker)
    {
        var expectedOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(null, _session.OfficeCode, _session.OfficeCode);
        var expectedTenantCode = TenantScopeCatalog.GetTenantCodeForOffice(expectedOfficeCode);
        return string.Equals(asset.ManagementId, marker, StringComparison.Ordinal) &&
               string.Equals(asset.ManagementNumber, marker, StringComparison.Ordinal) &&
               string.Equals(asset.MachineNumber, marker, StringComparison.Ordinal) &&
               asset.Notes.StartsWith(marker + "|", StringComparison.Ordinal) &&
               !asset.CustomerId.HasValue && !asset.ItemId.HasValue && !asset.BillingProfileId.HasValue &&
               !asset.LastBillingProfileId.HasValue &&
               string.IsNullOrWhiteSpace(asset.LastCustomerName) &&
               string.IsNullOrWhiteSpace(asset.LastInstallLocation) &&
               string.IsNullOrWhiteSpace(asset.LastBillingProfileDisplay) &&
               asset.LastAssignmentClearedAtUtc is null &&
               string.Equals(asset.TenantCode, expectedTenantCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(asset.OfficeCode, expectedOfficeCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(asset.ResponsibleOfficeCode, expectedOfficeCode, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> HasNoMultiPcRentalAssetDependenciesAsync(
        Guid assetId,
        LocalDbContext db)
    {
        if (await db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(history => history.AssetId == assetId))
        {
            return false;
        }

        var profiles = await db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToListAsync();
        foreach (var profile in profiles)
        {
            if (!HasNoMultiPcRentalAssetReferenceInBillingTemplate(
                    profile.BillingTemplateJson,
                    assetId))
                return false;
        }

        return true;
    }

    private static bool HasNoMultiPcRentalAssetReferenceInBillingTemplate(
        string? billingTemplateJson,
        Guid assetId)
    {
        if (assetId == Guid.Empty)
            return false;
        if (string.IsNullOrWhiteSpace(billingTemplateJson))
            return true;

        try
        {
            var parsed = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(
                billingTemplateJson,
                MultiPcJsonOptions);
            if (parsed is null)
                return false;

            using var document = JsonDocument.Parse(
                billingTemplateJson,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            var rawItemCount = 0;
            foreach (var rawItem in document.RootElement.EnumerateArray())
            {
                rawItemCount++;
                if (rawItem.ValueKind != JsonValueKind.Object)
                    return false;

                foreach (var property in rawItem.EnumerateObject())
                {
                    if (string.Equals(
                            property.Name,
                            nameof(RentalBillingTemplateItemModel.RepresentativeAssetId),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind == JsonValueKind.Null)
                            continue;
                        if (property.Value.ValueKind != JsonValueKind.String ||
                            !Guid.TryParse(property.Value.GetString(), out var representativeAssetId))
                        {
                            return false;
                        }

                        if (representativeAssetId == assetId)
                            return false;
                    }
                    else if (string.Equals(
                                 property.Name,
                                 nameof(RentalBillingTemplateItemModel.IncludedAssetIds),
                                 StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind == JsonValueKind.Null)
                            continue;
                        if (property.Value.ValueKind != JsonValueKind.Array)
                            return false;

                        foreach (var rawAssetId in property.Value.EnumerateArray())
                        {
                            if (rawAssetId.ValueKind != JsonValueKind.String ||
                                !Guid.TryParse(rawAssetId.GetString(), out var includedAssetId))
                            {
                                return false;
                            }

                            if (includedAssetId == assetId)
                                return false;
                        }
                    }
                }
            }

            return rawItemCount == parsed.Count &&
                   parsed.All(item => item is not null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private async Task<string> TryCleanupFailedMultiPcRentalAssetFixtureAsync(MultiPcE2EContext context, Guid assetId)
    {
        await _sync.TrySyncAsync();
        var marker = BuildMultiPcRentalAssetMarker(context.Contract.RunId);
        LocalRentalAsset? asset;
        await using (var db = new LocalDbContext())
        {
            asset = await db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(asset => asset.Id == assetId);
            if (asset is null)
                return "rental asset already absent";
            RequireMultiPc(
                IsMultiPcRentalAssetFixture(asset, marker) &&
                await HasNoMultiPcRentalAssetDependenciesAsync(assetId, db) &&
                !asset.IsDirty,
                "Failure cleanup refused because rental asset marker/dependency/session scope did not match.");
        }

        if (!asset.IsDeleted) { var delete = await _rental.DeleteAssetAsync(assetId, _session, asset.Revision); RequireMultiPc(delete.Success, "Failure cleanup rental asset delete failed."); await SyncMultiPcAndRequireCleanAsync("failure-rental-asset-delete"); }
        var deleted = await RequireMultiPcRentalAssetAsync(assetId, marker, expectedDeleted: true);
        var purge = await _api.PurgeRecycleBinAsync([new RecycleBinMutationTargetDto { EntityId = assetId, Kind = "rental-asset", ExpectedRevision = deleted.Revision }]);
        RequireMultiPc(purge is not null && purge.SucceededCount == 1, "Failure cleanup rental asset purge failed.");
        await SyncMultiPcAndRequireCleanAsync("failure-rental-asset-purge");
        RequireMultiPc(await _rental.GetAssetRowAsync(assetId, _session) is null, "Failure cleanup rental asset remains.");
        return "profile-free rental asset exact marker purged; dirty=0";
    }
}
