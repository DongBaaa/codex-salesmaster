using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.ViewModels;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingSelectionCacheTests
{
    [Fact]
    public void RentalBillingViewModel_StartCandidateAssetsLoad_ReusesCompletedCacheForSameSignature()
    {
        var profileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var customerId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var includedAssetId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var candidateAssetId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession())
        {
            LinkAssetsLater = true
        };
        var templateItem = new RentalBillingTemplateEditorItem
        {
            ItemId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            BillingLineMode = "\uBB36\uC74C"
        };
        vm.TemplateItems.Add(templateItem);
        vm.SelectedTemplateItem = templateItem;

        var includedPool = GetPrivateField<List<RentalBillingAssetOption>>(vm, "_includedAssetPool");
        includedPool.Add(new RentalBillingAssetOption
        {
            AssetId = includedAssetId,
            ItemName = "Included asset",
            IsLinkedToCurrentProfile = true
        });

        var candidatePool = GetPrivateField<List<RentalBillingAssetOption>>(vm, "_candidateAssetPool");
        candidatePool.Add(new RentalBillingAssetOption
        {
            AssetId = candidateAssetId,
            ItemName = "Candidate asset"
        });

        var signature = InvokePrivateInstance<string>(
            vm,
            "BuildCandidateAssetsLoadSignature",
            profileId,
            customerId,
            "Candidate customer",
            OfficeCodeCatalog.Usenet,
            false,
            false);
        InvokePrivateInstance(vm, "StoreCandidateAssetsLoadCache", signature);

        InvokePrivateInstance(
            vm,
            "StartCandidateAssetsLoad",
            profileId,
            customerId,
            "Candidate customer",
            OfficeCodeCatalog.Usenet,
            false,
            false);

        var included = Assert.Single(vm.IncludedAssets);
        Assert.Equal(includedAssetId, included.AssetId);
        var candidate = Assert.Single(vm.CandidateAssets);
        Assert.Equal(candidateAssetId, candidate.AssetId);
        Assert.Null(GetPrivateFieldValue(vm, "_candidateAssetsLoadCts"));
        Assert.Null(GetPrivateFieldValue(vm, "_candidateAssetsLoadTask"));
    }

    [Fact]
    public void RentalBillingViewModel_StartBillingHistoryRowsLoad_ReusesCompletedCacheForSameProfileSignature()
    {
        var profileId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        var billingRunId = Guid.Parse("77777777-7777-7777-7777-777777777777");
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession());
        var row = new RentalBillingViewRow
        {
            SelectionId = profileId,
            HasPersistedProfile = true,
            Source = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet
            }
        };
        var histories = new List<RentalBillingHistoryRow>
        {
            new()
            {
                BillingProfileId = profileId,
                BillingRunId = billingRunId,
                PeriodLabel = "2026-07",
                ScheduledDate = new DateOnly(2026, 7, 25)
            }
        };

        var signature = InvokePrivateInstance<string>(vm, "BuildBillingHistoryLoadSignature", row);
        InvokePrivateInstance(vm, "StoreBillingHistoryLoadCache", signature, histories);

        InvokePrivateInstance(vm, "StartBillingHistoryRowsLoad", row);

        var history = Assert.Single(vm.BillingHistoryRows);
        Assert.Equal(billingRunId, history.BillingRunId);
        Assert.Single(row.BillingHistoryRows);
        Assert.Null(GetPrivateFieldValue(vm, "_billingHistoryLoadCts"));
    }

    [Fact]
    public async Task RentalBillingViewModel_RefreshContractDateFromSourcesAsync_ReusesCompletedCacheForSameCustomerAssetSignature()
    {
        var customerId = Guid.Parse("88888888-8888-8888-8888-888888888888");
        var cachedDate = new DateOnly(2026, 7, 1);
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession())
        {
            EditCustomerId = customerId,
            EditOfficeCode = OfficeCodeCatalog.Usenet
        };

        var signature = InvokePrivateInstance<string>(vm, "BuildContractDateRefreshSignature");
        InvokePrivateInstance(vm, "StoreContractDateCache", signature, cachedDate);

        await InvokePrivateInstanceTaskAsync(
            vm,
            "RefreshContractDateFromSourcesAsync",
            false,
            false,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(cachedDate.ToDateTime(TimeOnly.MinValue), vm.EditContractDate);
        Assert.Equal(cachedDate.ToDateTime(TimeOnly.MinValue), vm.EditBillingStartDate);
    }

    [Fact]
    public void RentalBillingViewModel_CancelPendingLoadMethods_DoNotDisposeActiveTokens()
    {
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession());

        AssertCancellationSourceRemainsUsable(vm, "_candidateAssetsLoadCts", "CancelPendingCandidateAssetsLoad");
        AssertCancellationSourceRemainsUsable(vm, "_contractDateRefreshCts", "CancelPendingContractDateRefresh");
        AssertCancellationSourceRemainsUsable(vm, "_billingHistoryLoadCts", "CancelBillingHistoryLoad");
        AssertCancellationSourceRemainsUsable(vm, "_includedAssetHistoryLoadCts", "CancelIncludedAssetHistoryLoad");
        AssertCancellationSourceRemainsUsable(vm, "_filterReloadCts", "CancelPendingFilterReload");
    }

    [Fact]
    public void RentalBillingViewModel_SelectionLoadSignatures_RespectProfileCustomerAndOfficeBoundaries()
    {
        var profileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var anotherProfileId = Guid.Parse("12121212-1212-1212-1212-121212121212");
        var customerId = Guid.Parse("13131313-1313-1313-1313-131313131313");
        var anotherCustomerId = Guid.Parse("14141414-1414-1414-1414-141414141414");
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession())
        {
            EditCustomerId = customerId,
            EditOfficeCode = OfficeCodeCatalog.Usenet
        };

        var templateItem = new RentalBillingTemplateEditorItem
        {
            ItemId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")
        };
        var includedAssetId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        templateItem.IncludedAssetIds.Add(includedAssetId);
        vm.TemplateItems.Add(templateItem);
        vm.SelectedTemplateItem = templateItem;

        var candidateSignature = InvokePrivateInstance<string>(
            vm,
            "BuildCandidateAssetsLoadSignature",
            profileId,
            customerId,
            "Boundary customer",
            OfficeCodeCatalog.Usenet,
            false,
            false);
        var anotherProfileCandidateSignature = InvokePrivateInstance<string>(
            vm,
            "BuildCandidateAssetsLoadSignature",
            anotherProfileId,
            customerId,
            "Boundary customer",
            OfficeCodeCatalog.Usenet,
            false,
            false);
        var anotherCustomerCandidateSignature = InvokePrivateInstance<string>(
            vm,
            "BuildCandidateAssetsLoadSignature",
            profileId,
            anotherCustomerId,
            "Boundary customer",
            OfficeCodeCatalog.Usenet,
            false,
            false);
        var anotherOfficeCandidateSignature = InvokePrivateInstance<string>(
            vm,
            "BuildCandidateAssetsLoadSignature",
            profileId,
            customerId,
            "Boundary customer",
            OfficeCodeCatalog.Yeonsu,
            false,
            false);

        Assert.NotEqual(candidateSignature, anotherProfileCandidateSignature);
        Assert.NotEqual(candidateSignature, anotherCustomerCandidateSignature);
        Assert.NotEqual(candidateSignature, anotherOfficeCandidateSignature);

        var contractSignature = InvokePrivateInstance<string>(vm, "BuildContractDateRefreshSignature");
        templateItem.IncludedAssetIds.Add(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));
        var anotherAssetContractSignature = InvokePrivateInstance<string>(vm, "BuildContractDateRefreshSignature");
        templateItem.IncludedAssetIds.Clear();
        templateItem.IncludedAssetIds.Add(includedAssetId);
        vm.EditCustomerId = anotherCustomerId;
        var anotherCustomerContractSignature = InvokePrivateInstance<string>(vm, "BuildContractDateRefreshSignature");
        vm.EditCustomerId = customerId;
        vm.EditOfficeCode = OfficeCodeCatalog.Yeonsu;
        var anotherOfficeContractSignature = InvokePrivateInstance<string>(vm, "BuildContractDateRefreshSignature");

        Assert.NotEqual(contractSignature, anotherAssetContractSignature);
        Assert.NotEqual(contractSignature, anotherCustomerContractSignature);
        Assert.NotEqual(contractSignature, anotherOfficeContractSignature);

        var row = CreateBillingRow(profileId);
        var anotherRow = CreateBillingRow(anotherProfileId);
        var billingHistorySignature = InvokePrivateInstance<string>(vm, "BuildBillingHistoryLoadSignature", row);
        var anotherBillingHistorySignature = InvokePrivateInstance<string>(vm, "BuildBillingHistoryLoadSignature", anotherRow);

        Assert.NotEqual(billingHistorySignature, anotherBillingHistorySignature);
    }

    [Fact]
    public void RentalBillingViewModel_LoadCandidateAssetsAsync_RefreshesBillingAssetCollectionsOnlyOncePerLoad()
    {
        var source = ReadRentalBillingViewModelSource();
        var loadMethod = ExtractSourceBlock(
            source,
            "private async Task<bool> LoadCandidateAssetsAsync(",
            "private void CancelPendingSelectionLoads()");

        Assert.Single(Regex.Matches(loadMethod, "RefreshBillingAssetCollections\\(previousSelections\\);").Cast<Match>());
        Assert.Contains("StoreCandidateAssetsLoadCache(", loadMethod, StringComparison.Ordinal);
        Assert.Contains("var completedSignature = BuildCandidateAssetsLoadSignature(", loadMethod, StringComparison.Ordinal);
        Assert.Contains("StoreCandidateAssetsLoadCache(completedSignature);", loadMethod, StringComparison.Ordinal);
    }

    [Fact]
    public void RentalBillingViewModel_ReloadAndDeferredMaintenance_InvalidateSelectionCachesWithoutLegacyOnlyReload()
    {
        var source = ReadRentalBillingViewModelSource();
        var serializedReloadBody = ExtractSourceBlock(
            source,
            "private async Task RunSerializedReloadCoreAsync(CancellationToken ct)",
            "private async Task ReloadCoreAsync(CancellationToken ct)");
        var reloadBody = ExtractSourceBlock(
            source,
            "private async Task ReloadCoreAsync(CancellationToken ct)",
            "private bool ShouldPreserveSelectedEditorDuringReload()");
        var maintenanceBody = ExtractSourceBlock(
            source,
            "private async Task RunDeferredInitialMaintenanceAsync()",
            "public async Task LoadAndSelectProfileAsync(Guid profileId)");
        var selectionBody = ExtractSourceBlock(
            source,
            "partial void OnSelectedRowChanged(RentalBillingViewRow? value)",
            "private void RefreshBillingHistoryRows(RentalBillingViewRow? row)");
        var filterRequestBody = ExtractSourceBlock(
            source,
            "private void RequestFilterReload()",
            "private async Task RunDebouncedFilterReloadAsync(");
        var filterRunBody = ExtractSourceBlock(
            source,
            "private async Task RunDebouncedFilterReloadAsync(",
            "private void CancelPendingFilterReload()");

        Assert.Contains("CancelPendingSelectionLoads();", reloadBody, StringComparison.Ordinal);
        Assert.Contains("InvalidateSelectionLoadCaches();", reloadBody, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException) when (", serializedReloadBody, StringComparison.Ordinal);
        Assert.Contains("ct.IsCancellationRequested", serializedReloadBody, StringComparison.Ordinal);
        Assert.Contains("!_lifetimeCts.IsCancellationRequested", serializedReloadBody, StringComparison.Ordinal);
        Assert.Contains("!_isDisposed", serializedReloadBody, StringComparison.Ordinal);
        Assert.True(
            serializedReloadBody.IndexOf("_filterReloadGate.Release();", StringComparison.Ordinal) <
            serializedReloadBody.IndexOf("catch (OperationCanceledException)", StringComparison.Ordinal));
        Assert.Contains("if (_pendingFilterReload || repairResult is { HasChanges: true })", maintenanceBody, StringComparison.Ordinal);
        Assert.DoesNotContain("var hasMaintenanceChanges = cleanedLegacyAssignments > 0 || repairResult is { HasChanges: true };", maintenanceBody, StringComparison.Ordinal);
        Assert.Contains("StartSelectionDetailsLoad(value);", selectionBody, StringComparison.Ordinal);
        Assert.DoesNotContain("new CancellationTokenSource()", filterRequestBody, StringComparison.Ordinal);
        Assert.Contains("using var cts = new CancellationTokenSource();", filterRunBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RentalBillingViewModel_RunSerializedReloadCoreAsync_SwallowsOnlyActiveRequestSupersession()
    {
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession());
        using var supersededRequest = new CancellationTokenSource();
        supersededRequest.Cancel();

        var supersessionException = await Record.ExceptionAsync(() =>
            InvokePrivateInstanceTaskAsync(
                vm,
                "RunSerializedReloadCoreAsync",
                supersededRequest.Token));

        Assert.Null(supersessionException);

        vm.CancelPendingBackgroundWork();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokePrivateInstanceTaskAsync(
                vm,
                "RunSerializedReloadCoreAsync",
                CancellationToken.None));
    }

    [Fact]
    public async Task RentalBillingViewModel_CancelAndDrainPendingSelectionLoadsAsync_CancelsAndLeavesNoTrackedTasks()
    {
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession());
        var taskFields = new[]
        {
            "_candidateAssetsLoadTask",
            "_includedAssetHistoryLoadTask",
            "_billingHistoryLoadTask",
            "_contractDateRefreshTask"
        };
        var ctsFields = new[]
        {
            "_candidateAssetsLoadCts",
            "_includedAssetHistoryLoadCts",
            "_billingHistoryLoadCts",
            "_contractDateRefreshCts"
        };
        var sources = ctsFields.Select(_ => new CancellationTokenSource()).ToArray();
        var tasks = sources
            .Select(source => Task.Delay(Timeout.InfiniteTimeSpan, source.Token))
            .ToArray();

        try
        {
            for (var index = 0; index < taskFields.Length; index++)
            {
                SetPrivateField(vm, taskFields[index], tasks[index]);
                SetPrivateField(vm, ctsFields[index], sources[index]);
            }

            await InvokePrivateInstanceTaskAsync(vm, "CancelAndDrainPendingSelectionLoadsAsync");

            Assert.All(sources, source => Assert.True(source.IsCancellationRequested));
            Assert.All(tasks, task => Assert.True(task.IsCompleted));
            Assert.All(taskFields, field => Assert.Null(GetPrivateFieldValue(vm, field)));
            Assert.All(ctsFields, field => Assert.Null(GetPrivateFieldValue(vm, field)));
        }
        finally
        {
            foreach (var source in sources)
                source.Dispose();
        }
    }

    [Fact]
    public void RentalBillingViewModel_LoadAndSelectProfileAsync_SuppressesAndSerializesSharedContextSelectionLoads()
    {
        var source = ReadRentalBillingViewModelSource();
        var targetLoadBody = ExtractSourceBlock(
            source,
            "public async Task LoadAndSelectProfileAsync(Guid profileId)",
            "[RelayCommand]");
        var sequentialLoadBody = ExtractSourceBlock(
            source,
            "private async Task LoadSelectionDetailsCoreAsync(",
            "private async Task<string?> LoadCandidateAssetsForSelectionAsync(");
        var selectionChangedBody = ExtractSourceBlock(
            source,
            "partial void OnSelectedRowChanged(RentalBillingViewRow? value)",
            "private void UpdateSelectedCustomerGroupState(RentalBillingViewRow? row)");
        var includedAssetChangedBody = ExtractSourceBlock(
            source,
            "partial void OnSelectedIncludedAssetChanged(RentalBillingAssetOption? value)",
            "partial void OnSelectedIncludedAssetAssignmentHistoryChanged(");
        var candidateHelperBody = ExtractSourceBlock(
            source,
            "private async Task<string?> LoadCandidateAssetsForSelectionAsync(",
            "private async Task RefreshContractDateForSelectionAsync(");
        var contractHelperBody = ExtractSourceBlock(
            source,
            "private async Task RefreshContractDateForSelectionAsync(",
            "private void CancelPendingCandidateAssetsLoad()");

        Assert.Contains("await CancelAndDrainPendingSelectionLoadsAsync();", targetLoadBody, StringComparison.Ordinal);
        Assert.True(
            targetLoadBody.IndexOf("await CancelAndDrainPendingSelectionLoadsAsync();", StringComparison.Ordinal) <
            targetLoadBody.IndexOf("await _rental.RepairBillingInvoicePeriodLinksAsync", StringComparison.Ordinal));
        Assert.Contains("_suppressAutomaticSelectionLoads = true;", targetLoadBody, StringComparison.Ordinal);
        Assert.Contains("finally", targetLoadBody, StringComparison.Ordinal);
        Assert.Contains("_suppressAutomaticSelectionLoads = false;", targetLoadBody, StringComparison.Ordinal);
        Assert.Contains("await _selectionPipelineCoordinator.RunExclusiveAsync", targetLoadBody, StringComparison.Ordinal);
        Assert.Contains("await LoadSelectionDetailsCoreAsync(SelectedRow, targetVersion, pipelineToken);", targetLoadBody, StringComparison.Ordinal);
        Assert.Contains("await _filterReloadGate.WaitAsync(lifetimeToken);", targetLoadBody, StringComparison.Ordinal);
        Assert.Contains("_filterReloadGate.Release();", targetLoadBody, StringComparison.Ordinal);
        Assert.Contains("pipelineToken.ThrowIfCancellationRequested();", targetLoadBody, StringComparison.Ordinal);

        var historyIndex = sequentialLoadBody.IndexOf("await LoadBillingHistoryRowsForSelectionAsync(row, pipelineToken);", StringComparison.Ordinal);
        var candidateIndex = sequentialLoadBody.IndexOf("await LoadCandidateAssetsForSelectionAsync(", StringComparison.Ordinal);
        var includedHistoryIndex = sequentialLoadBody.IndexOf("await LoadIncludedAssetAssignmentHistoriesAsync(", StringComparison.Ordinal);
        var contractDateIndex = sequentialLoadBody.IndexOf("await RefreshContractDateForSelectionAsync(", StringComparison.Ordinal);
        Assert.True(historyIndex >= 0 && historyIndex < candidateIndex);
        Assert.True(candidateIndex < includedHistoryIndex);
        Assert.True(includedHistoryIndex < contractDateIndex);
        Assert.DoesNotContain("Task.WhenAll", sequentialLoadBody, StringComparison.Ordinal);

        Assert.Contains("StartSelectionDetailsLoad(value);", selectionChangedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("StartBillingHistoryRowsLoad(value);", selectionChangedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("StartCandidateAssetsLoad(", selectionChangedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ScheduleContractDateRefresh(", selectionChangedBody, StringComparison.Ordinal);
        Assert.Contains("_suppressIncludedAssetHistoryAutoLoad", includedAssetChangedBody, StringComparison.Ordinal);
        Assert.Contains("_candidateAssetsLoadCts = cts;", candidateHelperBody, StringComparison.Ordinal);
        Assert.Contains("_candidateAssetsLoadTask = task;", candidateHelperBody, StringComparison.Ordinal);
        Assert.Contains("cts.Token", candidateHelperBody, StringComparison.Ordinal);
        Assert.Contains("BuildCandidateAssetsLoadSignature(", candidateHelperBody, StringComparison.Ordinal);
        Assert.Contains("_contractDateRefreshCts = cts;", contractHelperBody, StringComparison.Ordinal);
        Assert.Contains("_contractDateRefreshTask = task;", contractHelperBody, StringComparison.Ordinal);
        Assert.Contains("RunScheduledContractDateRefreshAsync(", contractHelperBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectionPipelineCoordinator_AThenB_CancelsAndDrainsABeforeBEnters()
    {
        using var coordinator = new SelectionPipelineCoordinator();
        var aEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var aExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var taskA = coordinator.StartAsync(async (_, ct) =>
        {
            aEntered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                aExited.SetResult();
            }
        });
        await aEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var taskB = coordinator.StartAsync((_, _) =>
        {
            Assert.True(aExited.Task.IsCompleted);
            bEntered.SetResult();
            return Task.CompletedTask;
        });

        await taskB.WaitAsync(TimeSpan.FromSeconds(5));
        await bEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => taskA);
    }

    [Fact]
    public async Task SelectionPipelineCoordinator_ExclusiveAndSelectionOperations_NeverOverlap()
    {
        using var coordinator = new SelectionPipelineCoordinator();
        var exclusiveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExclusive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var maximumActive = 0;

        var exclusiveTask = coordinator.RunExclusiveAsync(async ct =>
        {
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            exclusiveEntered.SetResult();
            await releaseExclusive.Task.WaitAsync(ct);
            Interlocked.Decrement(ref active);
        }, CancellationToken.None);
        await exclusiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var selectionTask = coordinator.StartAsync((_, _) =>
        {
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            selectionEntered.SetResult();
            Interlocked.Decrement(ref active);
            return Task.CompletedTask;
        });
        await Task.Delay(50);
        Assert.False(selectionEntered.Task.IsCompleted);

        releaseExclusive.SetResult();
        await Task.WhenAll(exclusiveTask, selectionTask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task SelectionPipelineCoordinator_ExclusiveAfterCurrent_WaitsWithoutCancelingCurrentSelection()
    {
        using var coordinator = new SelectionPipelineCoordinator();
        var selectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSelection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var refreshEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectionWasCanceled = false;

        var selectionTask = coordinator.StartAsync(async (_, ct) =>
        {
            using var registration = ct.Register(() => selectionWasCanceled = true);
            selectionEntered.SetResult();
            await releaseSelection.Task;
        });
        await selectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var refreshTask = coordinator.RunExclusiveAfterCurrentAsync(_ =>
        {
            refreshEntered.SetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);
        await Task.Delay(50);
        Assert.False(refreshEntered.Task.IsCompleted);
        Assert.False(selectionWasCanceled);

        releaseSelection.SetResult();
        await Task.WhenAll(selectionTask, refreshTask).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(refreshEntered.Task.IsCompleted);
        Assert.False(selectionWasCanceled);
    }

    [Fact]
    public async Task SelectionPipelineCoordinator_CancelAfterSwallowedPhase_DoesNotEnterNextPhase()
    {
        using var coordinator = new SelectionPipelineCoordinator();
        var firstPhaseEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextPhaseEntered = false;

        var task = coordinator.StartAsync(async (_, ct) =>
        {
            firstPhaseEntered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }

            ct.ThrowIfCancellationRequested();
            nextPhaseEntered = true;
        });
        await firstPhaseEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.CancelCurrent();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(nextPhaseEntered);
    }

    [Fact]
    public async Task SelectionPipelineCoordinator_DisposeWhileWaitingForGate_CancelsWaiterWithoutEntry()
    {
        using var coordinator = new SelectionPipelineCoordinator();
        var exclusiveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseExclusive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterEntered = false;

        var exclusiveTask = coordinator.RunExclusiveAsync(async _ =>
        {
            exclusiveEntered.SetResult();
            await releaseExclusive.Task;
        }, CancellationToken.None);
        await exclusiveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waiterTask = coordinator.StartAsync((_, _) =>
        {
            waiterEntered = true;
            return Task.CompletedTask;
        });

        coordinator.Dispose();
        releaseExclusive.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => exclusiveTask);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiterTask);
        Assert.False(waiterEntered);
    }

    [Fact]
    public async Task SelectionPipelineCoordinator_PreviousFault_DoesNotPoisonNextSelection()
    {
        using var coordinator = new SelectionPipelineCoordinator();
        var faultEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var faultedTask = coordinator.StartAsync(async (_, _) =>
        {
            faultEntered.SetResult();
            await releaseFault.Task;
            throw new InvalidOperationException("synthetic");
        });
        await faultEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var nextEntered = false;
        var nextTask = coordinator.StartAsync((_, _) =>
        {
            nextEntered = true;
            return Task.CompletedTask;
        });
        releaseFault.SetResult();
        await nextTask.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => faultedTask);
        Assert.True(nextEntered);
    }

    [Fact]
    public void RentalBillingViewModel_AsyncSelectionResults_RevalidateSelectionAndSignaturesBeforeMutation()
    {
        var source = ReadRentalBillingViewModelSource();
        var candidateLoadBody = ExtractSourceBlock(
            source,
            "private async Task<bool> LoadCandidateAssetsAsync(",
            "public async Task RefreshAfterExternalAssetEditAsync(");
        var contractRefreshBody = ExtractSourceBlock(
            source,
            "private async Task RefreshContractDateFromSourcesAsync(",
            "private async Task<DateOnly?> ResolveContractDateFromSourcesAsync(");

        var candidateMutationIndex = candidateLoadBody.IndexOf("_includedAssetPool.Clear();", StringComparison.Ordinal);
        Assert.True(candidateMutationIndex > 0);
        Assert.True(candidateLoadBody.IndexOf("SelectedRow?.SelectionId != expectedSelectionId.Value", StringComparison.Ordinal) < candidateMutationIndex);
        Assert.True(candidateLoadBody.IndexOf("_activeCandidateAssetsLoadSignature", StringComparison.Ordinal) < candidateMutationIndex);
        Assert.True(candidateLoadBody.LastIndexOf("BuildCandidateAssetsLoadSignature(", candidateMutationIndex, StringComparison.Ordinal) >= 0);

        var contractApplyIndex = contractRefreshBody.IndexOf("SetContractReferenceDates(", StringComparison.Ordinal);
        var contractSignatureRecheckIndex = contractRefreshBody.IndexOf(
            "BuildContractDateRefreshSignature()",
            contractRefreshBody.IndexOf("await ResolveContractDateFromSourcesAsync", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.True(contractApplyIndex > 0);
        Assert.True(contractRefreshBody.IndexOf("SelectedRow?.SelectionId != baselineSelectionId.Value", StringComparison.Ordinal) < contractApplyIndex);
        Assert.True(contractSignatureRecheckIndex >= 0 && contractSignatureRecheckIndex < contractApplyIndex);
    }

    [Fact]
    public async Task RentalBillingViewModel_ProfileOnlySelection_PreservesPersistedContractDateWithoutCustomerOrAssets()
    {
        var profileId = Guid.NewGuid();
        var persistedContractDate = new DateOnly(2026, 7, 31);
        var vm = new RentalBillingViewModel(null!, null!, CreateAdminSession())
        {
            EditId = profileId,
            EditCustomerId = null,
            EditCustomerName = "Profile-only customer label",
            EditContractDate = persistedContractDate.ToDateTime(TimeOnly.MinValue)
        };
        Assert.Null(vm.EditCustomerId);
        Assert.Equal(persistedContractDate.ToDateTime(TimeOnly.MinValue), vm.EditContractDate);

        await InvokePrivateInstanceTaskAsync(
            vm,
            "RefreshContractDateForSelectionAsync",
            true,
            false,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(persistedContractDate.ToDateTime(TimeOnly.MinValue), vm.EditContractDate);

        var selectionDetailsBody = ExtractSourceBlock(
            ReadRentalBillingViewModelSource(),
            "private async Task LoadSelectionDetailsCoreAsync(",
            "private void ThrowIfSelectionPipelineInvalid(");
        Assert.Contains("preserveExistingValue: true", selectionDetailsBody, StringComparison.Ordinal);
        Assert.DoesNotContain("preserveExistingValue: false", selectionDetailsBody, StringComparison.Ordinal);
        var selectedRowChangedBody = ExtractSourceBlock(
            ReadRentalBillingViewModelSource(),
            "partial void OnSelectedRowChanged(RentalBillingViewRow? value)",
            "private void RefreshBillingHistoryRows(RentalBillingViewRow? row)");
        Assert.Contains(
            "SetContractReferenceDates(ToDateTime(source.ContractDate ?? source.BillingStartDate));",
            selectedRowChangedBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RentalBillingViewModel_ProfileSwitch_ClearsPreviousContractDateWhenNextProfileHasNoDate()
    {
        var rental = new RentalStateService(null!);
        var vm = new RentalBillingViewModel(rental, null!, CreateAdminSession());
        SetPrivateField(vm, "_suppressAutomaticSelectionLoads", true);
        var datedProfileId = Guid.NewGuid();
        var undatedProfileId = Guid.NewGuid();
        var contractDate = new DateOnly(2026, 7, 31);
        var datedRow = new RentalBillingViewRow
        {
            SelectionId = datedProfileId,
            HasPersistedProfile = true,
            Source = new LocalRentalBillingProfile
            {
                Id = datedProfileId,
                CustomerName = "Dated profile",
                ContractDate = contractDate,
                BillingStartDate = contractDate,
                BillingTemplateJson = "[]"
            }
        };
        var undatedRow = new RentalBillingViewRow
        {
            SelectionId = undatedProfileId,
            HasPersistedProfile = true,
            Source = new LocalRentalBillingProfile
            {
                Id = undatedProfileId,
                CustomerName = "Undated profile",
                ContractDate = null,
                BillingStartDate = null,
                BillingTemplateJson = "[]"
            }
        };

        vm.SelectedRow = datedRow;
        Assert.Equal(contractDate.ToDateTime(TimeOnly.MinValue), vm.EditContractDate);

        vm.SelectedRow = undatedRow;
        Assert.Null(vm.EditContractDate);
        Assert.Null(vm.EditBillingStartDate);
    }

    [Fact]
    public void RentalBillingViewModel_ExternalReload_PreservesDraftWhileRebindingLatestSelectedRow()
    {
        var profileId = Guid.NewGuid();
        var staleRevision = 101L;
        var winningRevision = 202L;
        var pendingNotes = "A-PENDING-RENTAL";
        var winningNotes = "B-WINS-RENTAL";
        var vm = new RentalBillingViewModel(new RentalStateService(null!), null!, CreateAdminSession());
        SetPrivateField(vm, "_suppressAutomaticSelectionLoads", true);
        var staleRow = new RentalBillingViewRow
        {
            SelectionId = profileId,
            HasPersistedProfile = true,
            Source = new LocalRentalBillingProfile
            {
                Id = profileId,
                Revision = staleRevision,
                CustomerName = "Concurrent rental profile",
                Notes = "INITIAL-RENTAL",
                BillingTemplateJson = "[]"
            }
        };
        var winningRow = new RentalBillingViewRow
        {
            SelectionId = profileId,
            HasPersistedProfile = true,
            Source = new LocalRentalBillingProfile
            {
                Id = profileId,
                Revision = winningRevision,
                CustomerName = "Concurrent rental profile",
                Notes = winningNotes,
                BillingTemplateJson = "[]"
            }
        };

        vm.SelectedRow = staleRow;
        var staleBaseline = GetPrivateField<string>(vm, "_selectedRowBaselineSignature");
        vm.EditNotes = pendingNotes;
        var pendingDraft = InvokePrivateInstance<RentalBillingEditorDraftModel>(
            vm,
            "BuildBillingEditorDraft");

        // A WPF collection reset can temporarily clear and then rebind SelectedItem.
        SetPrivateField(vm, "_autoSaveSuppressionCount", 1);
        vm.SelectedRow = null;
        vm.SelectedRow = winningRow;
        SetPrivateField(vm, "_autoSaveSuppressionCount", 0);
        Assert.Equal(winningNotes, vm.EditNotes);

        InvokePrivateInstance(
            vm,
            "PreserveEditorAfterReload",
            winningRow,
            pendingDraft,
            staleBaseline);

        Assert.Same(winningRow, vm.SelectedRow);
        Assert.Equal(winningRevision, vm.SelectedRow.Source.Revision);
        Assert.Equal(winningNotes, vm.SelectedRow.Source.Notes);
        Assert.Equal(pendingNotes, vm.EditNotes);
        var restoredDraft = InvokePrivateInstance<RentalBillingEditorDraftModel>(
            vm,
            "BuildBillingEditorDraft");
        Assert.Equal(staleRevision, restoredDraft.Revision);
        Assert.Equal(pendingNotes, restoredDraft.Notes);
        Assert.Equal(staleBaseline, GetPrivateField<string>(vm, "_selectedRowBaselineSignature"));

        var reloadBody = ExtractSourceBlock(
            ReadRentalBillingViewModelSource(),
            "private async Task ReloadCoreAsync(CancellationToken ct)",
            "private bool ShouldPreserveSelectedEditorDuringReload()");
        var requestVersionGuardIndex = reloadBody.IndexOf(
            "requestVersion != Volatile.Read(ref _filterReloadVersion)",
            StringComparison.Ordinal);
        var draftCaptureIndex = reloadBody.IndexOf("BuildBillingEditorDraft()", StringComparison.Ordinal);
        var rowsReplaceIndex = reloadBody.IndexOf("Rows.ReplaceWith(rows);", StringComparison.Ordinal);
        Assert.True(
            requestVersionGuardIndex >= 0 &&
            requestVersionGuardIndex < draftCaptureIndex &&
            draftCaptureIndex < rowsReplaceIndex);
        Assert.True(
            reloadBody.LastIndexOf("BeginAutoSaveSuppression();", rowsReplaceIndex, StringComparison.Ordinal) > draftCaptureIndex);
        Assert.True(
            reloadBody.LastIndexOf("_suppressAutomaticSelectionLoads = true;", rowsReplaceIndex, StringComparison.Ordinal) > draftCaptureIndex);
        Assert.Contains(
            "selectedEditorBaselineBeforeReload);",
            reloadBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RentalBillingViewModel_ExternalReload_MissingRowKeepsSafeOrphanDraftWithoutSelectionActions()
    {
        var profileId = Guid.NewGuid();
        var staleRevision = 301L;
        var pendingNotes = "A-PENDING-MISSING-RENTAL";
        var vm = new RentalBillingViewModel(new RentalStateService(null!), null!, CreateAdminSession());
        SetPrivateField(vm, "_suppressAutomaticSelectionLoads", true);
        var staleRow = new RentalBillingViewRow
        {
            SelectionId = profileId,
            HasPersistedProfile = true,
            Source = new LocalRentalBillingProfile
            {
                Id = profileId,
                Revision = staleRevision,
                CustomerName = "Missing concurrent rental profile",
                Notes = "INITIAL-MISSING-RENTAL",
                BillingTemplateJson = "[]"
            }
        };

        vm.SelectedRow = staleRow;
        var staleBaseline = GetPrivateField<string>(vm, "_selectedRowBaselineSignature");
        vm.EditNotes = pendingNotes;
        var pendingDraft = InvokePrivateInstance<RentalBillingEditorDraftModel>(
            vm,
            "BuildBillingEditorDraft");
        SetPrivateField(vm, "_autoSaveSuppressionCount", 2);
        SetPrivateField(vm, "_suppressAutomaticSelectionLoads", false);

        InvokePrivateInstance(
            vm,
            "PreserveEditorAfterReload",
            null,
            pendingDraft,
            staleBaseline);

        Assert.Null(vm.SelectedRow);
        Assert.Equal(pendingNotes, vm.EditNotes);
        Assert.True(GetPrivateField<bool>(vm, "_hasOrphanedEditorDraft"));
        Assert.Equal(staleBaseline, GetPrivateField<string>(vm, "_selectedRowBaselineSignature"));
        var restoredDraft = InvokePrivateInstance<RentalBillingEditorDraftModel>(
            vm,
            "BuildBillingEditorDraft");
        Assert.Equal(staleRevision, restoredDraft.Revision);
        Assert.Equal(pendingNotes, restoredDraft.Notes);
        Assert.False(vm.CanSave);
        Assert.False(vm.SaveCommand.CanExecute(null));
        Assert.False(vm.CanEditBillingProfileDetails);
        Assert.False(vm.CanDeleteSelected);
        Assert.False(vm.CanStartBillingSelected);
        Assert.False(vm.CanHoldSelected);
        Assert.False(vm.CanRegisterSettlementSelected);
        Assert.False(vm.CanMarkCompletedSelected);
        Assert.Equal(2, GetPrivateField<int>(vm, "_autoSaveSuppressionCount"));
        Assert.False(GetPrivateField<bool>(vm, "_suppressAutomaticSelectionLoads"));
        Assert.Contains("선택을 해제", vm.StatusMessage, StringComparison.Ordinal);
        Assert.Contains("안전한 임시본", vm.StatusMessage, StringComparison.Ordinal);
        Assert.False(await InvokePrivateInstanceTaskAsync<bool>(vm, "SaveCoreAsync"));
        Assert.Contains("임시본은 바로 저장할 수 없습니다", vm.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RentalBillingViewModel_FlushAutoSaveAsync_ReturnsTrueOnlyForPersistedCurrentDraft()
    {
        var appRoot = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "rental-autosave-result-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appRoot);

        try
        {
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={Path.Combine(appRoot, "autosave-result-tests.db")}")
                .Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var rental = new RentalStateService(db, local);
            var vm = new RentalBillingViewModel(rental, local, session);
            var defaultItem = InvokePrivateInstance<RentalBillingTemplateEditorItem>(
                vm,
                "CreateDefaultTemplateItem");
            vm.TemplateItems.Add(defaultItem);
            vm.SelectedTemplateItem = defaultItem;
            SetPrivateField(
                vm,
                "_selectedRowBaselineSignature",
                InvokePrivateInstance<string>(vm, "BuildCurrentEditorSignature"));

            Assert.False(await vm.FlushAutoSaveAsync());
            Assert.Null(await rental.GetBillingEditorDraftAsync(session));

            vm.EditNotes = "PERSISTED-AUTOSAVE-DRAFT";
            Assert.True(await vm.FlushAutoSaveAsync());
            var persisted = await rental.GetBillingEditorDraftAsync(session);
            Assert.NotNull(persisted);
            Assert.Equal("PERSISTED-AUTOSAVE-DRAFT", persisted.Notes);

            SetPrivateField(vm, "_autoSaveSuppressionCount", 1);
            vm.EditNotes = "SUPPRESSED-AUTOSAVE-DRAFT";
            Assert.False(await vm.FlushAutoSaveAsync());
            persisted = await rental.GetBillingEditorDraftAsync(session);
            Assert.NotNull(persisted);
            Assert.Equal("PERSISTED-AUTOSAVE-DRAFT", persisted.Notes);
            Assert.Equal(1, GetPrivateField<int>(vm, "_autoSaveSuppressionCount"));

            SetPrivateField(vm, "_autoSaveSuppressionCount", 0);
            var autoSaveGate = GetPrivateField<SemaphoreSlim>(vm, "_autoSaveGate");
            await autoSaveGate.WaitAsync();
            vm.EditNotes = "SUPPRESSED-WHILE-WAITING-FOR-GATE";
            var waitingFlush = vm.FlushAutoSaveAsync();
            SetPrivateField(vm, "_autoSaveSuppressionCount", 1);
            autoSaveGate.Release();
            Assert.False(await waitingFlush);
            persisted = await rental.GetBillingEditorDraftAsync(session);
            Assert.NotNull(persisted);
            Assert.Equal("PERSISTED-AUTOSAVE-DRAFT", persisted.Notes);
            Assert.Equal(1, GetPrivateField<int>(vm, "_autoSaveSuppressionCount"));
            vm.CancelPendingBackgroundWork();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(appRoot))
                Directory.Delete(appRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RentalBillingViewModel_ReloadCoreAsync_HydratesWinnerThenPreservesOrOrphansPendingDraft()
    {
        var appRoot = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "rental-reload-draft-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appRoot);

        try
        {
            var queryGate = new RentalBillingProfileQueryGate();
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={Path.Combine(appRoot, "reload-draft-tests.db")}")
                .AddInterceptors(queryGate)
                .Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var rental = new RentalStateService(db, local);
            var profileId = Guid.NewGuid();
            var profile = new LocalRentalBillingProfile
            {
                Id = profileId,
                Revision = 401L,
                IsDirty = false,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                CustomerName = "Reload behavior customer",
                InstallSiteName = "Reload behavior site",
                ItemName = "Reload behavior item",
                BillingStatus = "예정",
                Notes = "INITIAL-RELOAD",
                BillingTemplateJson = "[]",
                IsActive = true
            };
            db.RentalBillingProfiles.Add(profile);
            await db.SaveChangesAsync();

            var vm = new RentalBillingViewModel(rental, local, session);
            SetPrivateField(vm, "_suppressFilterReload", true);
            vm.ShowIndividualProfiles = true;
            SetPrivateField(vm, "_suppressFilterReload", false);
            SetPrivateField(vm, "_autoSaveSuppressionCount", 2);
            SetPrivateField(vm, "_suppressAutomaticSelectionLoads", true);
            await InvokePrivateInstanceTaskAsync(vm, "ReloadAsync");
            vm.SelectedRow = Assert.Single(vm.Rows, row => row.Source.Id == profileId);

            profile.Revision = 402L;
            profile.Notes = "B-WINNER-CLEAN";
            profile.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
            await db.SaveChangesAsync();
            await InvokePrivateInstanceTaskAsync(vm, "ReloadAsync");

            Assert.NotNull(vm.SelectedRow);
            Assert.Equal(402L, vm.SelectedRow.Source.Revision);
            Assert.Equal("B-WINNER-CLEAN", vm.EditNotes);
            Assert.False(GetPrivateField<bool>(vm, "_hasOrphanedEditorDraft"));

            var winnerBaseline = GetPrivateField<string>(vm, "_selectedRowBaselineSignature");
            profile.Revision = 403L;
            profile.Notes = "B-WINNER-WHILE-QUERY-RUNS";
            profile.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(2);
            await db.SaveChangesAsync();
            queryGate.Arm();
            var delayedReload = InvokePrivateInstanceTaskAsync(vm, "ReloadAsync");
            try
            {
                await queryGate.WaitUntilBlockedAsync(TimeSpan.FromSeconds(10));
                vm.EditNotes = "A-PENDING-DURING-QUERY";
            }
            finally
            {
                queryGate.Release();
            }
            await delayedReload;

            Assert.NotNull(vm.SelectedRow);
            Assert.Equal(403L, vm.SelectedRow.Source.Revision);
            Assert.Equal("B-WINNER-WHILE-QUERY-RUNS", vm.SelectedRow.Source.Notes);
            Assert.Equal("A-PENDING-DURING-QUERY", vm.EditNotes);
            Assert.Equal(winnerBaseline, GetPrivateField<string>(vm, "_selectedRowBaselineSignature"));
            var retainedDraft = InvokePrivateInstance<RentalBillingEditorDraftModel>(
                vm,
                "BuildBillingEditorDraft");
            Assert.Equal(402L, retainedDraft.Revision);

            profile.IsDeleted = true;
            profile.Revision = 404L;
            profile.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(3);
            await db.SaveChangesAsync();
            await InvokePrivateInstanceTaskAsync(vm, "ReloadAsync");

            Assert.Empty(vm.Rows);
            Assert.Null(vm.SelectedRow);
            Assert.Equal("A-PENDING-DURING-QUERY", vm.EditNotes);
            Assert.True(GetPrivateField<bool>(vm, "_hasOrphanedEditorDraft"));
            Assert.Equal(winnerBaseline, GetPrivateField<string>(vm, "_selectedRowBaselineSignature"));
            var orphanDraft = InvokePrivateInstance<RentalBillingEditorDraftModel>(
                vm,
                "BuildBillingEditorDraft");
            Assert.Equal(402L, orphanDraft.Revision);
            Assert.False(vm.CanSave);
            Assert.False(vm.CanEditBillingProfileDetails);
            Assert.False(vm.CanDeleteSelected);
            Assert.Equal(2, GetPrivateField<int>(vm, "_autoSaveSuppressionCount"));
            Assert.True(GetPrivateField<bool>(vm, "_suppressAutomaticSelectionLoads"));
            vm.CancelPendingBackgroundWork();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(appRoot))
                Directory.Delete(appRoot, recursive: true);
        }
    }

    [Fact]
    public void RentalBillingViewModel_MaintenanceAndExternalRefresh_UseCoordinatorAndRejectStaleContext()
    {
        var source = ReadRentalBillingViewModelSource();
        var maintenanceBody = ExtractSourceBlock(
            source,
            "private async Task RunDeferredInitialMaintenanceAsync()",
            "public async Task LoadAndSelectProfileAsync(Guid profileId)");
        var externalRefreshBody = ExtractSourceBlock(
            source,
            "public async Task RefreshAfterExternalAssetEditAsync(",
            "private bool IsExternalAssetRefreshContextCurrent(");
        var externalGuardBody = ExtractSourceBlock(
            source,
            "private bool IsExternalAssetRefreshContextCurrent(",
            "private void CancelPendingSelectionLoads()");

        Assert.Contains("await _filterReloadGate.WaitAsync(lifetimeToken);", maintenanceBody, StringComparison.Ordinal);
        Assert.Contains("await _selectionPipelineCoordinator.RunExclusiveAsync", maintenanceBody, StringComparison.Ordinal);
        Assert.Contains("await CancelAndDrainPhaseLoadsAsync();", maintenanceBody, StringComparison.Ordinal);
        Assert.True(Regex.Matches(maintenanceBody, "pipelineToken.ThrowIfCancellationRequested\\(\\);").Count >= 3);
        Assert.True(maintenanceBody.IndexOf("_filterReloadGate.Release();", StringComparison.Ordinal) < maintenanceBody.IndexOf("await ReloadAsync();", StringComparison.Ordinal));
        Assert.Contains("StartSelectionDetailsLoad(selectedRowToRestart);", maintenanceBody, StringComparison.Ordinal);

        Assert.Contains("await _selectionPipelineCoordinator.RunExclusiveAfterCurrentAsync", externalRefreshBody, StringComparison.Ordinal);
        Assert.Contains("await CancelAndDrainPhaseLoadsAsync();", externalRefreshBody, StringComparison.Ordinal);
        Assert.Contains("await LoadCandidateAssetsForSelectionAsync(", externalRefreshBody, StringComparison.Ordinal);
        Assert.Contains("completedCandidateSignature", externalRefreshBody, StringComparison.Ordinal);
        Assert.Contains("candidateSignature = completedCandidateSignature;", externalRefreshBody, StringComparison.Ordinal);
        Assert.Contains("await LoadIncludedAssetAssignmentHistoriesAsync(", externalRefreshBody, StringComparison.Ordinal);
        Assert.Contains("validateExpectedRow: true", externalRefreshBody, StringComparison.Ordinal);
        Assert.DoesNotContain("await LoadCandidateAssetsAsync(", externalRefreshBody, StringComparison.Ordinal);
        Assert.True(Regex.Matches(externalRefreshBody, "IsExternalAssetRefreshContextCurrent\\(").Count >= 3);
        var finalGuardIndex = externalRefreshBody.LastIndexOf("IsExternalAssetRefreshContextCurrent(", StringComparison.Ordinal);
        Assert.True(finalGuardIndex >= 0 && finalGuardIndex < externalRefreshBody.IndexOf("StatusMessage =", finalGuardIndex, StringComparison.Ordinal));
        Assert.Contains("IncludedAssets.Any(asset => asset.AssetId == assetId)", externalRefreshBody, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusMessage = SelectedIncludedAsset?.AssetId == assetId", externalRefreshBody, StringComparison.Ordinal);

        Assert.Contains("ReferenceEquals(SelectedRow, selectedRow)", externalGuardBody, StringComparison.Ordinal);
        Assert.Contains("EditCustomerId == customerId", externalGuardBody, StringComparison.Ordinal);
        Assert.Contains("string.Equals(EditCustomerName, customerName", externalGuardBody, StringComparison.Ordinal);
        Assert.Contains("string.Equals(EditOfficeCode, officeCode", externalGuardBody, StringComparison.Ordinal);
        Assert.Contains("BuildCandidateAssetsLoadSignature(", externalGuardBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RentalBillingViewModel_BackgroundDetailStarters_UseCoordinatorOrderingAndStaleGuards()
    {
        var source = ReadRentalBillingViewModelSource();
        var applyCustomerBody = ExtractSourceBlock(
            source,
            "public void ApplySelectedCustomer(LocalCustomer customer)",
            "private void StartSelectedCustomerCandidateAndContractRefresh()");
        var customerRefreshBody = ExtractSourceBlock(
            source,
            "private void StartSelectedCustomerCandidateAndContractRefresh()",
            "private bool IsSelectedCustomerRefreshContextCurrent(");
        var scheduleContractBody = ExtractSourceBlock(
            source,
            "private void ScheduleContractDateRefresh(",
            "private bool IsScheduledContractRefreshContextCurrent(");
        var includedChangedBody = ExtractSourceBlock(
            source,
            "partial void OnSelectedIncludedAssetChanged(RentalBillingAssetOption? value)",
            "partial void OnSelectedIncludedAssetAssignmentHistoryChanged(");
        var includedHistoryCoreBody = ExtractSourceBlock(
            source,
            "private async Task LoadIncludedAssetAssignmentHistoriesCoreAsync(",
            "private void CancelIncludedAssetHistoryLoad()");
        var refreshCustomerContextBody = ExtractSourceBlock(
            source,
            "public async Task RefreshSelectedCustomerContextAsync()",
            "private bool IsSelectedCustomerContextCurrent(");
        var refreshCustomerGuardBody = ExtractSourceBlock(
            source,
            "private bool IsSelectedCustomerContextCurrent(",
            "private void SelectRow(Guid entityId)");

        Assert.Contains("StartSelectedCustomerCandidateAndContractRefresh();", applyCustomerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("StartCandidateAssetsLoad(", applyCustomerBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ScheduleContractDateRefresh(", applyCustomerBody, StringComparison.Ordinal);
        Assert.Contains("_selectionPipelineCoordinator.StartAsync", customerRefreshBody, StringComparison.Ordinal);
        var candidateIndex = customerRefreshBody.IndexOf("await LoadCandidateAssetsForSelectionAsync(", StringComparison.Ordinal);
        var contractIndex = customerRefreshBody.IndexOf("await RefreshContractDateForSelectionAsync(", StringComparison.Ordinal);
        Assert.True(candidateIndex >= 0 && candidateIndex < contractIndex);
        Assert.True(Regex.Matches(customerRefreshBody, "IsSelectedCustomerRefreshContextCurrent\\(").Count >= 2);

        Assert.Contains("RunExclusiveAfterCurrentAsync", scheduleContractBody, StringComparison.Ordinal);
        Assert.Contains("await CancelAndDrainPhaseLoadsAsync();", scheduleContractBody, StringComparison.Ordinal);
        Assert.Contains("IsScheduledContractRefreshContextCurrent(", scheduleContractBody, StringComparison.Ordinal);
        Assert.Contains("await RefreshContractDateForSelectionAsync(", scheduleContractBody, StringComparison.Ordinal);

        Assert.Contains("RunExclusiveAfterCurrentAsync", includedChangedBody, StringComparison.Ordinal);
        Assert.Contains("requestedSelectionId", includedChangedBody, StringComparison.Ordinal);
        Assert.Contains("SelectedIncludedAsset?.AssetId != requestedAssetId", includedChangedBody, StringComparison.Ordinal);
        Assert.Contains("validateExpectedRow: true", includedChangedBody, StringComparison.Ordinal);
        var historyMutationIndex = includedHistoryCoreBody.IndexOf("ApplyIncludedAssetAssignmentHistoriesForDisplay(histories);", StringComparison.Ordinal);
        Assert.True(historyMutationIndex > 0);
        Assert.True(includedHistoryCoreBody.IndexOf("validateExpectedRow && !ReferenceEquals(SelectedRow, expectedRow)", StringComparison.Ordinal) < historyMutationIndex);
        Assert.True(includedHistoryCoreBody.IndexOf("SelectedIncludedAsset?.AssetId != assetId", StringComparison.Ordinal) < historyMutationIndex);

        Assert.Contains("RunExclusiveAfterCurrentAsync", refreshCustomerContextBody, StringComparison.Ordinal);
        Assert.Contains("await CancelAndDrainPhaseLoadsAsync();", refreshCustomerContextBody, StringComparison.Ordinal);
        Assert.Contains("GetCustomerForRentalScopeAsync(", refreshCustomerContextBody, StringComparison.Ordinal);
        Assert.Contains("await RefreshContractDateForSelectionAsync(", refreshCustomerContextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("await RefreshContractDateFromSourcesAsync(", refreshCustomerContextBody, StringComparison.Ordinal);
        Assert.True(Regex.Matches(refreshCustomerContextBody, "IsSelectedCustomerContextCurrent\\(").Count >= 3);
        Assert.Contains("ReferenceEquals(SelectedRow, requestedRow)", refreshCustomerGuardBody, StringComparison.Ordinal);
        Assert.Contains("EditCustomerId == requestedCustomerId", refreshCustomerGuardBody, StringComparison.Ordinal);
    }

    [Fact]
    public void RentalBillingViewModel_UserEntryPoints_SerializeSharedDatabaseWorkAndRejectStaleContext()
    {
        var source = ReadRentalBillingViewModelSource();
        var saveBody = ExtractSourceBlock(
            source,
            "private async Task<bool> SaveCoreAsync()",
            "private bool IsSaveContextCurrent(");
        var manualCandidateBody = ExtractSourceBlock(
            source,
            "private async Task RefreshCandidateAssetsAsync()",
            "private void ApplySelectedAssetsToTemplate()");
        var customerLookupBody = ExtractSourceBlock(
            source,
            "public async Task<IReadOnlyList<LookupRow>> BuildCustomerLookupRowsAsync()",
            "public void ApplySelectedCustomer(LocalCustomer customer)");

        Assert.Contains("RunExclusiveAfterCurrentAsync", manualCandidateBody, StringComparison.Ordinal);
        Assert.Contains("await CancelAndDrainPhaseLoadsAsync();", manualCandidateBody, StringComparison.Ordinal);
        Assert.Contains("await LoadCandidateAssetsForSelectionAsync(", manualCandidateBody, StringComparison.Ordinal);
        Assert.Contains("completedCandidateSignature", manualCandidateBody, StringComparison.Ordinal);
        Assert.True(Regex.Matches(manualCandidateBody, "IsCandidateRefreshContextCurrent\\(").Count >= 2);
        Assert.DoesNotContain("await LoadCandidateAssetsAsync(", manualCandidateBody, StringComparison.Ordinal);

        var lookupGateIndex = customerLookupBody.IndexOf("RunExclusiveAfterCurrentAsync", StringComparison.Ordinal);
        var lookupQueryIndex = customerLookupBody.IndexOf("await _local.GetCustomersForRentalScopeAsync(", StringComparison.Ordinal);
        var lookupGateEndIndex = customerLookupBody.IndexOf("}, _lifetimeCts.Token);", lookupQueryIndex, StringComparison.Ordinal);
        Assert.True(lookupGateIndex >= 0 && lookupGateIndex < lookupQueryIndex && lookupQueryIndex < lookupGateEndIndex);
        Assert.Contains("sessionId = _session.SessionId", customerLookupBody, StringComparison.Ordinal);
        Assert.Contains("sessionScopeEpoch = _session.SyncScopeEpoch", customerLookupBody, StringComparison.Ordinal);
        Assert.True(Regex.Matches(customerLookupBody, "IsCustomerLookupContextCurrent\\(").Count >= 2);
        Assert.Contains("pipelineToken", customerLookupBody, StringComparison.Ordinal);

        var saveGateIndex = saveBody.IndexOf("RunExclusiveAfterCurrentAsync", StringComparison.Ordinal);
        var contractIndex = saveBody.IndexOf("await RefreshContractDateForSelectionAsync(", StringComparison.Ordinal);
        var saveDatabaseIndex = saveBody.IndexOf("await _rental.SaveBillingProfileAsync(", StringComparison.Ordinal);
        var saveGateEndIndex = saveBody.IndexOf("}, _lifetimeCts.Token);", saveDatabaseIndex, StringComparison.Ordinal);
        var resultHandlingIndex = saveBody.IndexOf("if (!result.Success)", StringComparison.Ordinal);
        var missingContractWarningIndex = saveBody.IndexOf("if (showMissingContractDateWarning)", StringComparison.Ordinal);
        var reloadIndex = saveBody.IndexOf("await ReloadAsync();", StringComparison.Ordinal);
        Assert.True(saveGateIndex >= 0 && saveGateIndex < contractIndex && contractIndex < saveDatabaseIndex);
        Assert.True(saveDatabaseIndex < saveGateEndIndex && saveGateEndIndex < resultHandlingIndex && resultHandlingIndex < reloadIndex);
        Assert.True(resultHandlingIndex < missingContractWarningIndex && missingContractWarningIndex < reloadIndex);
        Assert.Contains("ct: pipelineToken", saveBody, StringComparison.Ordinal);
        Assert.Contains("saveContextStayedCurrent = IsSaveSelectionCurrent", saveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("await RefreshContractDateFromSourcesAsync(", saveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshEditRevisionFromStoreAsync", saveBody, StringComparison.Ordinal);
        Assert.DoesNotContain("GetBillingProfileRevisionAsync(", source, StringComparison.Ordinal);
        Assert.Contains("reloadedRow.Source.Id == result.EntityId", saveBody, StringComparison.Ordinal);
        Assert.Contains("reloadedRow.SelectionId == result.EntityId", saveBody, StringComparison.Ordinal);
        Assert.Contains("_editRevision = reloadedRow.Source.Revision;", saveBody, StringComparison.Ordinal);
        Assert.Contains("_editRevision = 0;", saveBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RentalStateService_LegacyColumnProbe_DoesNotDisposeDbContextOwnedConnection()
    {
        var appRoot = Path.Combine(
            FindRepositoryRoot(),
            "temp",
            "rental-connection-ownership-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appRoot);

        try
        {
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite($"Data Source={Path.Combine(appRoot, "거래플랜-tests.db")}")
                .Options;
            await using var db = new LocalDbContext(options);
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var session = CreateAdminSession();
            var local = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var rental = new RentalStateService(db, local);

            await rental.CleanupLegacyAssignedUsernamesAsync();

            _ = await db.Customers.AsNoTracking().CountAsync();
            Assert.NotEqual(System.Data.ConnectionState.Broken, db.Database.GetDbConnection().State);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(appRoot))
                Directory.Delete(appRoot, recursive: true);
        }
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static RentalBillingViewRow CreateBillingRow(Guid profileId)
        => new()
        {
            SelectionId = profileId,
            HasPersistedProfile = true,
            Source = new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet
            }
        };

    private static T GetPrivateField<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(target));
    }

    private static object? GetPrivateFieldValue(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target);
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static void InvokePrivateInstance(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(target, args);
    }

    private static T InvokePrivateInstance<T>(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<T>(method!.Invoke(target, args));
    }

    private static async Task InvokePrivateInstanceTaskAsync(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(target, args);
        var task = Assert.IsAssignableFrom<Task>(result);
        await task;
    }

    private static async Task<T> InvokePrivateInstanceTaskAsync<T>(
        object target,
        string methodName,
        params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = method!.Invoke(target, args);
        var task = Assert.IsAssignableFrom<Task<T>>(result);
        return await task;
    }

    private sealed class RentalBillingProfileQueryGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource<bool> _blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public Task WaitUntilBlockedAsync(TimeSpan timeout)
            => _blocked.Task.WaitAsync(timeout);

        public void Release() => _released.TrySetResult(true);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) == 1 &&
                command.CommandText.Contains("RentalBillingProfiles", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Exchange(ref _armed, 0) == 1)
            {
                _blocked.TrySetResult(true);
                await _released.Task.WaitAsync(cancellationToken);
            }

            return result;
        }
    }

    private static string ReadRentalBillingViewModelSource()
    {
        var root = FindRepositoryRoot();
        var sourcePath = Path.Combine(
            root,
            "Desktop",
            "\uAC70\uB798\uD50C\uB79C.Desktop.App",
            "ViewModels",
            "RentalBillingViewModel.cs");
        return File.ReadAllText(sourcePath);
    }

    private static string ExtractSourceBlock(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found after start: {endMarker}");
        return source[start..end];
    }

    private static void AssertCancellationSourceRemainsUsable(object target, string fieldName, string cancelMethodName)
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        SetPrivateField(target, fieldName, cts);

        InvokePrivateInstance(target, cancelMethodName);

        var exception = Record.Exception(() =>
        {
            using var registration = token.Register(static () => { });
        });

        Assert.Null(exception);
        cts.Dispose();
    }

    private static string FindRepositoryRoot()
    {
        foreach (var seedPath in new[]
                 {
                     Environment.GetEnvironmentVariable("GEORAEPLAN_REPOSITORY_ROOT"),
                     AppContext.BaseDirectory,
                     Environment.CurrentDirectory
                 }.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            var current = new DirectoryInfo(seedPath!);
            while (current is not null)
            {
                var candidate = Path.Combine(current.FullName, "Desktop", "\uAC70\uB798\uD50C\uB79C.Desktop.App");
                if (Directory.Exists(candidate))
                    return current.FullName;

                current = current.Parent;
            }
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
