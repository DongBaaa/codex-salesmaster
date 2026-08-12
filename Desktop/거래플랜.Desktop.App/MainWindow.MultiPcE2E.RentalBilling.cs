using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App;

public partial class MainWindow
{
    private const string MultiPcRentalBillingTemplateItemName = "MULTIPC-RENTAL-PROFILE-ONLY";

    // This fixture deliberately has one embedded marker template row, no asset references,
    // and no billing-run API usage. It remains a profile-only concurrency test:
    // no invoice, payment, or journal mutation.
    private async Task RunMultiPcRentalBillingRoleAAsync(
        MultiPcE2EContext context,
        ICollection<MultiPcE2EStep> steps)
    {
        var marker = BuildMultiPcRentalBillingMarker(context.Contract.RunId);
        var pendingNotes = $"A-PENDING-RENTAL-{context.Contract.RunId}";
        var winningNotes = $"B-WINS-RENTAL-{context.Contract.RunId}";
        var vm = new RentalBillingViewModel(_rental, _local, _session, _api);
        await vm.LoadAsync();
        var window = ShowMultiPcRentalBillingWindow(vm);
        Guid profileId;
        try
        {
            vm.EditCustomerName = marker;
            vm.EditInstallLocation = marker;
            RequireMultiPc(
                vm.SelectedTemplateItem is not null,
                "Rental billing fixture template editor row was missing.");
            vm.SelectedTemplateItem!.DisplayItemName = MultiPcRentalBillingTemplateItemName;
            vm.LinkAssetsLater = true;
            vm.EditOfficeCode = _session.OfficeCode;
            vm.EditContractDate = DateTime.Today;
            vm.EditNotes = $"INITIAL-RENTAL-{context.Contract.RunId}";
            profileId = vm.EditId;
            context.OwnedRentalBillingProfileId = profileId;
            await vm.SaveCommand.ExecuteAsync(null);
            RequireMultiPc(vm.SelectedRow?.Source.Id == profileId,
                $"Rental billing fixture UI save failed: {vm.StatusMessage}");
        }
        finally
        {
            window.DataContext = null;
            CloseWindowForSmoke(window);
        }

        await SyncMultiPcAndRequireCleanAsync("A-rental-create-sync");
        var created = await RequireMultiPcRentalBillingProfileAsync(profileId, false, context);
        RequireMultiPc(
            string.Equals(created.CustomerName, marker, StringComparison.Ordinal) &&
            string.Equals(created.Notes, $"INITIAL-RENTAL-{context.Contract.RunId}", StringComparison.Ordinal) &&
            !created.IsDirty,
            "PC-A rental billing fixture did not reach a clean state.");
        await WriteMultiPcRentalBillingSignalAsync(context, "rental-a-created.json", profileId, created.Revision, created.Notes);
        AddPassedStep(steps, "rental-billing-create-and-sync", "profile-only fixture saved through RentalBillingViewModel; dirty=false");

        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "rental-b-loaded.json", TimeSpan.FromSeconds(120), "B", signal => signal.RentalBillingProfileId == profileId);
        var stale = await RequireMultiPcRentalBillingProfileAsync(profileId, false, context);
        var staleVm = new RentalBillingViewModel(_rental, _local, _session, _api);
        await staleVm.LoadAndSelectProfileAsync(profileId);
        var staleWindow = ShowMultiPcRentalBillingWindow(staleVm);
        try
        {
            staleVm.EditNotes = pendingNotes;
            var stagedDraftPersisted = await staleVm.FlushAutoSaveAsync();
            RequireMultiPc(
                stagedDraftPersisted &&
                staleVm.SelectedRow?.Source.Id == profileId &&
                string.Equals(staleVm.EditNotes, pendingNotes, StringComparison.Ordinal),
                "PC-A could not persist the rental billing stale editor draft.");
            await WriteMultiPcRentalBillingSignalAsync(context, "rental-a-staged.json", profileId, stale.Revision, pendingNotes);
            AddPassedStep(steps, "rental-billing-stale-edit-staged", "RentalBillingViewModel selection and autosave draft retained");

            var bWritten = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "rental-b-written.json", TimeSpan.FromSeconds(120), "B", signal => signal.RentalBillingProfileId == profileId);
            var stalePush = await PushMultiPcStaleRentalBillingProfileAsync(stale, pendingNotes, false, context.Contract.RunId);
            RequireMultiPc(IsMultiPcRentalBillingConflict(stalePush, profileId), "Runner-owned server did not reject the stale rental billing save.");
            AddPassedStep(steps, "rental-billing-actual-server-stale-save", "accepted=0; conflicts=1; expected-revision mismatch");

            await SyncMultiPcAndRequireCleanAsync("A-rental-pull-winner");
            var settlement = await WaitForMultiPcRentalBillingStaleDraftSettlementAsync(
                staleVm,
                profileId,
                stale.Revision,
                bWritten.Revision,
                pendingNotes,
                winningNotes,
                TimeSpan.FromSeconds(15));
            var afterConflict = await RequireMultiPcRentalBillingProfileAsync(profileId, false, context);
            RequireMultiPc(
                staleVm.SelectedRow?.Source.Id == profileId &&
                staleVm.SelectedRow.Source.Revision == bWritten.Revision &&
                string.Equals(staleVm.EditNotes, pendingNotes, StringComparison.Ordinal) &&
                settlement.DraftProfileId == profileId &&
                settlement.DraftRevision == stale.Revision &&
                string.Equals(settlement.DraftNotes, pendingNotes, StringComparison.Ordinal) &&
                afterConflict.Revision == bWritten.Revision &&
                string.Equals(afterConflict.Notes, winningNotes, StringComparison.Ordinal) &&
                string.Equals(bWritten.Value, winningNotes, StringComparison.Ordinal),
                "PC-A rental billing stale draft was not preserved after the conflict pull.");
            AddPassedStep(steps, "rental-billing-stale-save-conflict", "stale draft/selection preserved; local winner remains clean");
            await staleVm.ClearAutoSaveDraftAsync();
            await staleVm.LoadAndSelectProfileAsync(profileId);
            staleVm.LinkAssetsLater = true;
            RequireMultiPc(staleVm.SelectedRow?.Source.Id == profileId && staleVm.SelectedRow.Source.Revision == bWritten.Revision && string.Equals(staleVm.EditNotes, winningNotes, StringComparison.Ordinal),
                "PC-A rental billing reload did not replace the stale editor baseline.");
            var retryNotes = $"A-RETRY-RENTAL-{context.Contract.RunId}";
            staleVm.EditNotes = retryNotes;
            await staleVm.SaveCommand.ExecuteAsync(null);
            RequireMultiPc(staleVm.SelectedRow?.Source.Id == profileId && string.Equals(staleVm.EditNotes, retryNotes, StringComparison.Ordinal),
                $"PC-A rental billing retry through the ViewModel did not save: {staleVm.StatusMessage}");
            await SyncMultiPcAndRequireCleanAsync("A-rental-retry-sync");
            var retried = await RequireMultiPcRentalBillingProfileAsync(profileId, false, context);
            RequireMultiPc(
                retried.Revision > bWritten.Revision &&
                string.Equals(retried.Notes, retryNotes, StringComparison.Ordinal) &&
                !retried.IsDirty,
                "Rental billing retry did not reach the exact server-clean value.");
            await WriteMultiPcRentalBillingSignalAsync(context, "rental-a-retried.json", profileId, retried.Revision, retried.Notes);
            AddPassedStep(steps, "rental-billing-pull-reload-retry-save", "pull/reload then RentalBillingViewModel retry succeeded; dirty=false");

            var bDeleted = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "rental-b-deleted.json", TimeSpan.FromSeconds(120), "B", signal => signal.RentalBillingProfileId == profileId);
            var staleDeletePush = await PushMultiPcStaleRentalBillingProfileAsync(retried, retried.Notes, true, context.Contract.RunId);
            RequireMultiPc(
                IsMultiPcRentalBillingIdempotentDeleteNoOp(staleDeletePush, profileId, bDeleted.Revision),
                "Runner-owned server did not preserve the existing rental billing tombstone for an idempotent stale delete no-op.");
            AddPassedStep(
                steps,
                "rental-billing-server-idempotent-stale-delete-no-op",
                "accepted=1; conflicts=0; accepted revision equals the existing PC-B tombstone revision");

            await SyncMultiPcAndRequireCleanAsync("A-rental-pull-delete");
            var deleted = await RequireMultiPcRentalBillingProfileAsync(profileId, true, context);
            RequireMultiPc(
                deleted.Revision == bDeleted.Revision &&
                deleted.Revision > retried.Revision &&
                deleted.IsDeleted &&
                !deleted.IsDirty &&
                string.Equals(deleted.CustomerName, retried.CustomerName, StringComparison.Ordinal) &&
                string.Equals(deleted.InstallSiteName, retried.InstallSiteName, StringComparison.Ordinal) &&
                string.Equals(deleted.Notes, retried.Notes, StringComparison.Ordinal) &&
                string.Equals(deleted.BillingTemplateJson, retried.BillingTemplateJson, StringComparison.Ordinal) &&
                string.Equals(deleted.BillingRunsJson, retried.BillingRunsJson, StringComparison.Ordinal),
                "Rental billing idempotent stale delete no-op did not preserve the PC-B tombstone revision, content, and deletion state.");
            await WriteMultiPcRentalBillingSignalAsync(context, "rental-a-delete-observed.json", profileId, deleted.Revision, "deleted");
            AddPassedStep(
                steps,
                "rental-billing-idempotent-delete-propagation",
                "PC-B tombstone revision/content/deleted state preserved after pull; dirty=false");
            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "rental-b-purged.json", TimeSpan.FromSeconds(120), "B", signal => signal.RentalBillingProfileId == profileId);
            await SyncMultiPcAndRequireCleanAsync("A-rental-pull-purge");
            RequireMultiPc(await _local.GetRentalBillingProfileAsync(profileId, _session) is null, "PC-A rental billing purge cleanup did not converge.");
            await WriteMultiPcRentalBillingSignalAsync(context, "rental-a-clean.json", profileId, deleted.Revision, "purged");
            AddPassedStep(steps, "rental-billing-fixture-purge-no-residue", "profile-only fixture absent; no billing run/invoice/payment created");
        }
        finally
        {
            staleWindow.DataContext = null;
            CloseWindowForSmoke(staleWindow);
        }
    }

    private async Task RunMultiPcRentalBillingRoleBAsync(MultiPcE2EContext context, ICollection<MultiPcE2EStep> steps)
    {
        var createdSignal = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "rental-a-created.json", TimeSpan.FromSeconds(120), "A", signal => signal.RentalBillingProfileId != Guid.Empty);
        var profileId = createdSignal.RentalBillingProfileId;
        await SyncMultiPcAndRequireCleanAsync("B-rental-pull-created");
        var loaded = await RequireMultiPcRentalBillingProfileAsync(profileId, false, context);
        RequireMultiPc(IsMultiPcRentalBillingFixture(loaded, context), "PC-B refused an unverified rental billing signal fixture before mutation.");
        context.OwnedRentalBillingProfileId = profileId;
        var vm = new RentalBillingViewModel(_rental, _local, _session, _api);
        await vm.LoadAndSelectProfileAsync(profileId);
        vm.LinkAssetsLater = true;
        var window = ShowMultiPcRentalBillingWindow(vm);
        try
        {
            await WriteMultiPcRentalBillingSignalAsync(context, "rental-b-loaded.json", profileId, loaded.Revision, loaded.Notes);
            AddPassedStep(steps, "rental-billing-cross-client-pull", "RentalBillingViewModel selected pulled profile; dirty=false");
            _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "rental-a-staged.json", TimeSpan.FromSeconds(120), "A", signal => signal.RentalBillingProfileId == profileId);
            vm.EditNotes = $"B-WINS-RENTAL-{context.Contract.RunId}";
            await vm.SaveCommand.ExecuteAsync(null);
            RequireMultiPc(vm.SelectedRow?.Source.Id == profileId, $"PC-B rental billing UI save failed: {vm.StatusMessage}");
        }
        finally { window.DataContext = null; CloseWindowForSmoke(window); }
        await SyncMultiPcAndRequireCleanAsync("B-rental-write-sync");
        var written = await RequireMultiPcRentalBillingProfileAsync(profileId, false, context);
        RequireMultiPc(
            written.Revision > loaded.Revision &&
            string.Equals(written.Notes, $"B-WINS-RENTAL-{context.Contract.RunId}", StringComparison.Ordinal),
            "PC-B rental billing winner value was not acknowledged exactly.");
        await WriteMultiPcRentalBillingSignalAsync(context, "rental-b-written.json", profileId, written.Revision, written.Notes);
        AddPassedStep(steps, "rental-billing-winner-save-and-sync", "PC-B ViewModel value won; dirty=false");
        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "rental-a-retried.json", TimeSpan.FromSeconds(120), "A", signal => signal.RentalBillingProfileId == profileId);
        await SyncMultiPcAndRequireCleanAsync("B-rental-pull-retry");
        var latest = await RequireMultiPcRentalBillingProfileAsync(profileId, false, context);
        RequireMultiPc(IsMultiPcRentalBillingFixture(latest, context), "PC-B refused rental billing delete because marker/scope/dependency verification failed.");
        RequireMultiPc(
            latest.Revision > written.Revision &&
            string.Equals(latest.Notes, $"A-RETRY-RENTAL-{context.Contract.RunId}", StringComparison.Ordinal),
            "PC-B did not pull the latest rental billing retry before delete.");
        var delete = await _rental.DeleteBillingProfileAsync(profileId, _session, latest.Revision);
        RequireMultiPc(delete.Success, $"PC-B rental billing delete failed: {delete.Message}");
        await SyncMultiPcAndRequireCleanAsync("B-rental-delete-sync");
        var deleted = await RequireMultiPcRentalBillingProfileAsync(profileId, true, context);
        RequireMultiPc(
            deleted.Revision > latest.Revision,
            "PC-B rental billing delete did not advance the server revision.");
        await WriteMultiPcRentalBillingSignalAsync(context, "rental-b-deleted.json", profileId, deleted.Revision, "deleted");
        AddPassedStep(steps, "rental-billing-delete-and-sync", "profile deleted through RentalStateService; dirty=false");
        _ = await WaitForMultiPcPayloadAsync<MultiPcSignal>(context, "rental-a-delete-observed.json", TimeSpan.FromSeconds(120), "A", signal => signal.RentalBillingProfileId == profileId);
        var deletedForPurge = await RequireMultiPcRentalBillingProfileAsync(profileId, true, context);
        RequireMultiPc(IsMultiPcRentalBillingFixture(deletedForPurge, context), "PC-B refused rental billing purge because marker/scope/dependency verification failed.");
        var purge = await _api.PurgeRecycleBinAsync([new RecycleBinMutationTargetDto { EntityId = profileId, Kind = "rental-billing-profile", ExpectedRevision = deletedForPurge.Revision }]);
        RequireMultiPc(purge is not null && purge.RequestedCount == 1 && purge.SucceededCount == 1, "Server rental billing fixture purge failed.");
        await SyncMultiPcAndRequireCleanAsync("B-rental-pull-purge");
        RequireMultiPc(await _local.GetRentalBillingProfileAsync(profileId, _session) is null, "PC-B rental billing fixture remains after purge.");
        await WriteMultiPcRentalBillingSignalAsync(context, "rental-b-purged.json", profileId, deleted.Revision, "purged");
        AddPassedStep(steps, "server-rental-billing-fixture-purge", "requested=1; succeeded=1; profile-only fixture absent");
    }

    private RentalBillingWindow ShowMultiPcRentalBillingWindow(RentalBillingViewModel vm)
    {
        var window = new RentalBillingWindow(vm) { Owner = this, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        window.Show();
        return window;
    }

    private async Task<MultiPcRentalBillingSettlementObservation> WaitForMultiPcRentalBillingStaleDraftSettlementAsync(
        RentalBillingViewModel vm,
        Guid profileId,
        long staleRevision,
        long winningRevision,
        string pendingNotes,
        string winningNotes,
        TimeSpan timeout)
    {
        const int requiredStableUiObservations = 3;
        var draftSettingKey = BuildMultiPcRentalBillingDraftSettingKey();
        MultiPcRentalBillingSettlementObservation? last = null;
        var tracker = new MultiPcRentalBillingSettlementTracker(requiredStableUiObservations);

        return await RunMultiPcRentalBillingBoundedOperationAsync(
            async timeoutToken =>
            {
                while (true)
                {
                    timeoutToken.ThrowIfCancellationRequested();
                    var editor = await Dispatcher.InvokeAsync(
                        () => new
                        {
                            vm.IsBusy,
                            SelectedProfileId = vm.SelectedRow?.Source.Id,
                            SelectedRevision = vm.SelectedRow?.Source.Revision ?? 0,
                            EditorProfileId = vm.EditId,
                            EditorNotes = vm.EditNotes
                        },
                        DispatcherPriority.ContextIdle,
                        timeoutToken);

                    await using var db = new LocalDbContext();
                    var local = await db.RentalBillingProfiles
                        .IgnoreQueryFilters()
                        .AsNoTracking()
                        .FirstOrDefaultAsync(current => current.Id == profileId, timeoutToken);
                    var draftPayload = await db.Settings
                        .AsNoTracking()
                        .Where(setting => setting.Key == draftSettingKey)
                        .Select(setting => setting.Value)
                        .FirstOrDefaultAsync(timeoutToken);
                    var draft = DeserializeMultiPcRentalBillingDraft(draftPayload);

                    last = new MultiPcRentalBillingSettlementObservation(
                        editor.IsBusy,
                        editor.SelectedProfileId,
                        editor.SelectedRevision,
                        editor.EditorProfileId,
                        editor.EditorNotes,
                        local?.Id,
                        local?.Revision ?? 0,
                        local?.Notes ?? string.Empty,
                        local?.IsDirty ?? true,
                        local?.IsDeleted ?? true,
                        draft?.EditId,
                        draft?.Revision,
                        draft?.Notes ?? string.Empty);

                    var action = tracker.Observe(
                        last,
                        IsMultiPcRentalBillingStaleDraftSettlement(
                            last,
                            profileId,
                            staleRevision,
                            winningRevision,
                            pendingNotes,
                            winningNotes));
                    if (action == MultiPcRentalBillingSettlementAction.Complete)
                        return last;

                    if (action == MultiPcRentalBillingSettlementAction.FlushCurrentEditorDraft)
                    {
                        // Re-persist only after the UI and local winner have remained stable.
                        // The post-flush observations therefore verify the ViewModel's current
                        // editor revision, rather than merely the pre-refresh draft row.
                        var currentEditorDraftPersisted = await vm.FlushAutoSaveAsync(timeoutToken);
                        tracker.MarkCurrentEditorDraftPersisted(currentEditorDraftPersisted);
                        continue;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(100), timeoutToken);
                }
            },
            timeout,
            () =>
                "PC-A rental billing external refresh did not settle with the exact winner and persisted stale draft. " +
                $"selectedRevision={last?.SelectedRevision ?? 0}; localRevision={last?.LocalRevision ?? 0}; " +
                $"draftRevision={last?.DraftRevision ?? 0}; busy={last?.IsBusy ?? false}; " +
                $"editorDraftPersisted={tracker.CurrentEditorDraftPersisted}; stable={tracker.StableObservationCount}.");
    }

    internal static async Task<T> RunMultiPcRentalBillingBoundedOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout,
        Func<string> timeoutDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(timeoutDiagnostic);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            return await operation(timeoutCts.Token);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(timeoutDiagnostic(), ex);
        }
    }

    private string BuildMultiPcRentalBillingDraftSettingKey()
    {
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            _session.OfficeCode,
            DomainConstants.OfficeUsenet);
        var username = (_session.User?.Username ?? "anonymous").Trim();
        if (string.IsNullOrWhiteSpace(username))
            username = "anonymous";

        return $"Rental.BillingEditorDraft.{officeCode}.{username}".ToUpperInvariant();
    }

    private static RentalBillingEditorDraftModel? DeserializeMultiPcRentalBillingDraft(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<RentalBillingEditorDraftModel>(
                payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static bool IsMultiPcRentalBillingStaleDraftSettlement(
        MultiPcRentalBillingSettlementObservation observation,
        Guid profileId,
        long staleRevision,
        long winningRevision,
        string pendingNotes,
        string winningNotes)
        => !observation.IsBusy &&
           observation.SelectedProfileId == profileId &&
           observation.SelectedRevision == winningRevision &&
           observation.EditorProfileId == profileId &&
           string.Equals(observation.EditorNotes, pendingNotes, StringComparison.Ordinal) &&
           observation.LocalProfileId == profileId &&
           observation.LocalRevision == winningRevision &&
           string.Equals(observation.LocalNotes, winningNotes, StringComparison.Ordinal) &&
           !observation.LocalIsDirty &&
           !observation.LocalIsDeleted &&
           observation.DraftProfileId == profileId &&
           observation.DraftRevision == staleRevision &&
           string.Equals(observation.DraftNotes, pendingNotes, StringComparison.Ordinal);

    internal sealed record MultiPcRentalBillingSettlementObservation(
        bool IsBusy,
        Guid? SelectedProfileId,
        long SelectedRevision,
        Guid EditorProfileId,
        string EditorNotes,
        Guid? LocalProfileId,
        long LocalRevision,
        string LocalNotes,
        bool LocalIsDirty,
        bool LocalIsDeleted,
        Guid? DraftProfileId,
        long? DraftRevision,
        string DraftNotes);

    internal enum MultiPcRentalBillingSettlementAction
    {
        Pending,
        FlushCurrentEditorDraft,
        Complete
    }

    internal sealed class MultiPcRentalBillingSettlementTracker
    {
        private readonly int _requiredStableObservations;
        private MultiPcRentalBillingSettlementObservation? _previous;

        internal MultiPcRentalBillingSettlementTracker(int requiredStableObservations)
        {
            if (requiredStableObservations <= 0)
                throw new ArgumentOutOfRangeException(nameof(requiredStableObservations));

            _requiredStableObservations = requiredStableObservations;
        }

        internal int StableObservationCount { get; private set; }

        internal bool CurrentEditorDraftPersisted { get; private set; }

        internal MultiPcRentalBillingSettlementAction Observe(
            MultiPcRentalBillingSettlementObservation observation,
            bool matchesExpectedState)
        {
            ArgumentNullException.ThrowIfNull(observation);
            if (!matchesExpectedState)
            {
                StableObservationCount = 0;
                _previous = observation;
                return MultiPcRentalBillingSettlementAction.Pending;
            }

            StableObservationCount = Equals(observation, _previous)
                ? StableObservationCount + 1
                : 1;
            _previous = observation;
            if (StableObservationCount < _requiredStableObservations)
                return MultiPcRentalBillingSettlementAction.Pending;

            return CurrentEditorDraftPersisted
                ? MultiPcRentalBillingSettlementAction.Complete
                : MultiPcRentalBillingSettlementAction.FlushCurrentEditorDraft;
        }

        internal void MarkCurrentEditorDraftPersisted(bool persisted)
        {
            if (!persisted)
            {
                throw new InvalidOperationException(
                    "PC-A rental billing current editor draft was not persisted after the winner refresh.");
            }

            CurrentEditorDraftPersisted = true;
            StableObservationCount = 0;
            _previous = null;
        }
    }

    private async Task<LocalRentalBillingProfile> RequireMultiPcRentalBillingProfileAsync(
        Guid id,
        bool deleted,
        MultiPcE2EContext context)
    {
        await using var db = new LocalDbContext();
        var profile = await db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == id);
        RequireMultiPc(profile is not null && profile.IsDeleted == deleted, "Rental billing fixture state did not match the expected deletion state.");
        var verifiedProfile = profile!;
        RequireMultiPc(
            IsMultiPcRentalBillingFixture(
                verifiedProfile,
                id,
                context.Contract.RunId) &&
            await HasNoMultiPcRentalBillingDependenciesAsync(id, db) &&
            !verifiedProfile.IsDirty,
            "Rental billing fixture marker/scope/dependency/dirty verification failed.");
        return verifiedProfile;
    }

    private async Task<SyncPushResult?> PushMultiPcStaleRentalBillingProfileAsync(LocalRentalBillingProfile profile, string notes, bool deleted, string runId)
    {
        var dto = LocalMappings.ToDto(profile);
        dto.Notes = notes;
        dto.IsDeleted = deleted;
        dto.ExpectedRevision = profile.Revision;
        dto.Revision = profile.Revision;
        dto.MutationId = $"multipc-{runId}-rental-stale-{Guid.NewGuid():N}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;
        var deviceId = (await _local.GetSettingAsync("Sync.DeviceId") ?? string.Empty).Trim();
        return await _api.PushAsync(new SyncPushRequest { DeviceId = deviceId, RentalBillingProfiles = [dto] });
    }

    private static bool IsMultiPcRentalBillingConflict(SyncPushResult? push, Guid id)
        => push is { AcceptedCount: 0, ConflictCount: 1 } &&
           push.Conflicts.Count(conflict =>
               string.Equals(conflict.EntityName, "RentalBillingProfile", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(conflict.EntityId, id.ToString("D"), StringComparison.OrdinalIgnoreCase) &&
               conflict.Reason.StartsWith("Expected revision mismatch.", StringComparison.Ordinal)) == 1;

    private static bool IsMultiPcRentalBillingIdempotentDeleteNoOp(
        SyncPushResult? push,
        Guid id,
        long tombstoneRevision)
        => push is { AcceptedCount: 1, ConflictCount: 0 } &&
           push.Conflicts.Count == 0 &&
           push.AcceptedRevisions.Count == 1 &&
           push.AcceptedRevisions.Count(accepted =>
               string.Equals(accepted.EntityName, "RentalBillingProfile", StringComparison.OrdinalIgnoreCase) &&
               accepted.EntityId == id &&
               accepted.Revision == tombstoneRevision) == 1;

    private static string BuildMultiPcRentalBillingMarker(string runId)
        => $"CODEX-MULTIPC-{new string(runId.Where(char.IsLetterOrDigit).Take(16).ToArray())}-RENTAL";

    private bool IsMultiPcRentalBillingFixture(
        LocalRentalBillingProfile profile,
        MultiPcE2EContext context)
        => IsMultiPcRentalBillingFixture(profile, profile.Id, context.Contract.RunId);

    private bool IsMultiPcRentalBillingFixture(
        LocalRentalBillingProfile profile,
        Guid expectedProfileId,
        string runId)
    {
        var marker = BuildMultiPcRentalBillingMarker(runId);
        var expectedOfficeCode = OfficeCodeCatalog.ResolveOwningOfficeCode(
            null,
            _session.OfficeCode,
            _session.OfficeCode);
        var expectedTenantCode = TenantScopeCatalog.GetTenantCodeForOffice(expectedOfficeCode);
        var allowedNotes = new HashSet<string>(StringComparer.Ordinal)
        {
            $"INITIAL-RENTAL-{runId}",
            $"B-WINS-RENTAL-{runId}",
            $"A-RETRY-RENTAL-{runId}"
        };
        return profile.Id == expectedProfileId &&
               string.Equals(profile.CustomerName, marker, StringComparison.Ordinal) &&
               string.Equals(profile.InstallSiteName, marker, StringComparison.Ordinal) &&
               string.Equals(profile.TenantCode, expectedTenantCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(profile.OfficeCode, expectedOfficeCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(profile.ResponsibleOfficeCode, expectedOfficeCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(profile.ManagementCompanyCode, expectedOfficeCode, StringComparison.OrdinalIgnoreCase) &&
               !profile.CustomerId.HasValue &&
               string.Equals(profile.ItemName, MultiPcRentalBillingTemplateItemName, StringComparison.Ordinal) &&
               profile.MonthlyAmount == 0m &&
               profile.DepositAmount == 0m &&
               profile.SettledAmount == 0m &&
               profile.OutstandingAmount == 0m &&
               allowedNotes.Contains(profile.Notes) &&
               HasOnlyMultiPcRentalBillingTemplate(profile.BillingTemplateJson) &&
               string.Equals(profile.BillingRunsJson, "[]", StringComparison.Ordinal);
    }

    private static bool HasOnlyMultiPcRentalBillingTemplate(string? billingTemplateJson)
    {
        if (string.IsNullOrWhiteSpace(billingTemplateJson))
            return false;

        try
        {
            using var document = JsonDocument.Parse(billingTemplateJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            var elements = document.RootElement.EnumerateArray().ToList();
            if (elements.Count != 1 || elements[0].ValueKind != JsonValueKind.Object)
                return false;

            var allowedProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "ItemId",
                "DisplayItemName",
                "BillingLineMode",
                "IndividualGroupingMode",
                "Specification",
                "Unit",
                "MaterialNumber",
                "Quantity",
                "UnitPrice",
                "Amount",
                "Note",
                "IncludedAssetIds"
            };
            var properties = elements[0].EnumerateObject().ToList();
            if (properties.Count != allowedProperties.Count ||
                properties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != allowedProperties.Count ||
                properties.Any(property => !allowedProperties.Contains(property.Name)))
            {
                return false;
            }

            var templateItems = JsonSerializer.Deserialize<List<RentalBillingTemplateItemModel>>(billingTemplateJson);
            if (templateItems is not { Count: 1 })
                return false;

            var item = templateItems[0];
            return item.ItemId != Guid.Empty &&
                   !item.CatalogItemId.HasValue &&
                   string.Equals(item.DisplayItemName, MultiPcRentalBillingTemplateItemName, StringComparison.Ordinal) &&
                   string.Equals(item.BillingLineMode, "묶음", StringComparison.Ordinal) &&
                   string.Equals(
                       item.IndividualGroupingMode,
                       RentalBillingTemplateItemModel.IndividualGroupingByModel,
                       StringComparison.Ordinal) &&
                   string.IsNullOrEmpty(item.Specification) &&
                   string.IsNullOrEmpty(item.Unit) &&
                   string.IsNullOrEmpty(item.MaterialNumber) &&
                   !item.RepresentativeAssetId.HasValue &&
                   item.Quantity == 1m &&
                   item.UnitPrice == 0m &&
                   item.Amount == 0m &&
                   string.IsNullOrEmpty(item.Note) &&
                   item.IncludedAssetIds is { Count: 0 };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task<bool> HasNoMultiPcRentalBillingDependenciesAsync(
        Guid profileId,
        LocalDbContext db)
    {
        if (profileId == Guid.Empty)
            return false;

        if (await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(invoice => invoice.LinkedRentalBillingProfileId == profileId))
        {
            return false;
        }

        if (await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(transaction => transaction.LinkedRentalBillingProfileId == profileId))
        {
            return false;
        }

        if (await db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(asset =>
                    asset.BillingProfileId == profileId ||
                    asset.LastBillingProfileId == profileId))
        {
            return false;
        }

        if (await db.RentalAssetAssignmentHistories
                .IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(history => history.BillingProfileId == profileId))
        {
            return false;
        }

        return !await db.RentalBillingLogs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .AnyAsync(log => log.BillingProfileId == profileId);
    }

    private async Task<string> TryCleanupFailedMultiPcRentalBillingFixtureAsync(MultiPcE2EContext context, Guid profileId)
    {
        await _sync.TrySyncAsync();
        LocalRentalBillingProfile? profile;
        await using (var db = new LocalDbContext())
        {
            profile = await db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .AsNoTracking()
                .FirstOrDefaultAsync(current => current.Id == profileId);
            if (profile is null)
                return "rental billing profile already absent";
            RequireMultiPc(
                IsMultiPcRentalBillingFixture(profile, context) &&
                await HasNoMultiPcRentalBillingDependenciesAsync(profileId, db) &&
                !profile.IsDirty,
                "Failure cleanup refused because rental billing fixture marker/scope/dependency/dirty verification failed.");
        }

        if (!profile.IsDeleted)
        {
            var delete = await _rental.DeleteBillingProfileAsync(profileId, _session, profile.Revision);
            RequireMultiPc(delete.Success, "Failure cleanup rental billing delete failed.");
            await SyncMultiPcAndRequireCleanAsync("failure-rental-billing-delete");
            profile = await RequireMultiPcRentalBillingProfileAsync(profileId, true, context);
        }
        profile = await RequireMultiPcRentalBillingProfileAsync(profileId, true, context);
        var purge = await _api.PurgeRecycleBinAsync([new RecycleBinMutationTargetDto { EntityId = profileId, Kind = "rental-billing-profile", ExpectedRevision = profile.Revision }]);
        RequireMultiPc(purge is not null && purge.SucceededCount == 1, "Failure cleanup rental billing purge failed.");
        await SyncMultiPcAndRequireCleanAsync("failure-rental-billing-purge");
        RequireMultiPc(await _local.GetRentalBillingProfileAsync(profileId, _session) is null, "Failure cleanup rental billing profile remains.");
        return "rental billing profile exact marker purged; dirty=0";
    }
}
