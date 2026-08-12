using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class EnvironmentSettingsViewModel
{
    private const string RecycleBinFilterAll = "ALL";
    private const int RecycleBinServerMutationBatchSize = 40;
    private List<RecycleBinEntry> _allRecycleBinEntries = new();

    private sealed class RecycleBinMirrorResult
    {
        public List<RecycleBinEntry> SucceededEntries { get; } = new();
        public List<string> Failures { get; } = new();
        public Dictionary<
            (RecycleBinEntityKind Kind, Guid EntityId),
            List<string>>
            EntryFailures { get; } = new();
        public Dictionary<
            (Guid EntityId, RecycleBinEntityKind Kind),
            LocalStateService.ServerPurgeConfirmationFence>
            PurgeConfirmationFences { get; } = new();
        public bool RequiresAuthoritativeRefresh { get; set; }
        public bool HasAmbiguousRestoreOutcome { get; set; }

        public void AddEntryFailure(
            RecycleBinEntry entry,
            string message)
        {
            Failures.Add(message);
            var key = (entry.Kind, entry.EntityId);
            if (!EntryFailures.TryGetValue(
                    key,
                    out var messages))
            {
                messages = new List<string>();
                EntryFailures.Add(key, messages);
            }

            messages.Add(message);
        }
    }

    internal sealed record RecycleBinSuccessfulLocalPurge(
        RecycleBinEntry Entry,
        LocalStateService.ServerPurgeConfirmationFence?
            ConfirmationFence);

    internal sealed record RecycleBinPurgeCascadeReconciliation(
        IReadOnlySet<(
            RecycleBinEntityKind Kind,
            Guid EntityId)> CoveredEntries,
        IReadOnlyList<string> RemainingServerFailures)
    {
        public int SucceededCount =>
            CoveredEntries.Count;
    }

    internal sealed record RecycleBinConfirmedRestoreLocalApplyResult(
        int SucceededCount,
        IReadOnlyList<string> Failures,
        bool HasLocalApplyFailure,
        bool RequiresAuthoritativeRefresh,
        bool AuthoritativeRefreshSucceeded,
        string? AuthoritativeRefreshFailure);

    [ObservableProperty] private DisplayOption? _selectedRecycleBinTypeOption;
    [ObservableProperty] private string _recycleBinSearchText = string.Empty;
    [ObservableProperty] private RecycleBinEntry? _selectedRecycleBinEntry;
    [ObservableProperty] private int _recycleBinTotalCount;
    [ObservableProperty] private int _recycleBinCustomerCount;
    [ObservableProperty] private int _recycleBinContractCount;
    [ObservableProperty] private int _recycleBinItemCount;
    [ObservableProperty] private int _recycleBinCompanyProfileCount;
    [ObservableProperty] private int _recycleBinCustomerCategoryCount;
    [ObservableProperty] private int _recycleBinPriceGradeOptionCount;
    [ObservableProperty] private int _recycleBinTradeTypeOptionCount;
    [ObservableProperty] private int _recycleBinItemCategoryOptionCount;
    [ObservableProperty] private int _recycleBinInvoiceCount;
    [ObservableProperty] private int _recycleBinPaymentCount;
    [ObservableProperty] private int _recycleBinTransactionCount;
    [ObservableProperty] private int _recycleBinInventoryTransferCount;
    [ObservableProperty] private int _recycleBinRentalManagementCompanyCount;
    [ObservableProperty] private int _recycleBinRentalBillingProfileCount;
    [ObservableProperty] private int _recycleBinRentalAssetCount;
    [ObservableProperty] private int _recycleBinRentalBillingLogCount;
    [ObservableProperty] private int _markedRecycleBinCount;
    [ObservableProperty] private string _recycleBinSummary = "휴지통이 비어 있습니다.";
    [ObservableProperty] private string _selectedRecycleBinDependencySummary = "삭제 차단 사유를 확인하려면 항목을 선택하세요.";
    [ObservableProperty] private RecycleBinCustomerMergeCandidate? _selectedRecycleBinMergeTarget;
    [ObservableProperty] private bool _isRecycleBinDependencyBusy;

    public ObservableCollection<DisplayOption> RecycleBinTypeOptions { get; } = new();
    public ObservableCollection<RecycleBinEntry> RecycleBinEntries { get; } = new();
    public ObservableCollection<RecycleBinDependencyItem> SelectedRecycleBinDependencies { get; } = new();
    public ObservableCollection<RecycleBinCustomerMergeCandidate> SelectedRecycleBinMergeCandidates { get; } = new();

    public bool HasRecycleBinEntries => RecycleBinEntries.Count > 0;
    public bool HasSelectedRecycleBinEntry => SelectedRecycleBinEntry is not null;
    public bool HasMarkedRecycleBinEntries => MarkedRecycleBinCount > 0;
    public bool HasSelectedRecycleBinDependencies => SelectedRecycleBinDependencies.Count > 0;
    public bool HasSelectedRecycleBinMergeCandidates => SelectedRecycleBinMergeCandidates.Count > 0;
    public bool CanManageRecycleBinData => _session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.DataBackupRestore);
    public bool CanReloadRecycleBin => CanManageRecycleBinData;
    public bool CanMarkFilteredRecycleBinEntries => CanManageRecycleBinData && HasRecycleBinEntries;
    public bool CanMutateSelectedRecycleBinEntry => CanManageRecycleBinData && HasSelectedRecycleBinEntry;
    public bool CanMutateMarkedRecycleBinEntries => CanManageRecycleBinData && HasMarkedRecycleBinEntries;
    public bool CanMergeSelectedRecycleBinCustomer => SelectedRecycleBinEntry?.Kind == RecycleBinEntityKind.Customer &&
                                                      CanManageRecycleBinData &&
                                                      SelectedRecycleBinEntry is not null &&
                                                      SelectedRecycleBinMergeTarget is not null;

    partial void OnSelectedRecycleBinTypeOptionChanged(DisplayOption? value) => ApplyRecycleBinFilter();
    partial void OnRecycleBinSearchTextChanged(string value) => ApplyRecycleBinFilter();
    partial void OnSelectedRecycleBinEntryChanged(RecycleBinEntry? value)
    {
        OnPropertyChanged(nameof(HasSelectedRecycleBinEntry));
        OnPropertyChanged(nameof(CanMutateSelectedRecycleBinEntry));
        OnPropertyChanged(nameof(CanMergeSelectedRecycleBinCustomer));
        _ = LoadSelectedRecycleBinContextAsync(value);
    }
    partial void OnMarkedRecycleBinCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasMarkedRecycleBinEntries));
        OnPropertyChanged(nameof(CanMutateMarkedRecycleBinEntries));
    }
    partial void OnSelectedRecycleBinMergeTargetChanged(RecycleBinCustomerMergeCandidate? value)
        => OnPropertyChanged(nameof(CanMergeSelectedRecycleBinCustomer));

    private void InitializeRecycleBinTypeOptions()
    {
        if (RecycleBinTypeOptions.Count > 0)
            return;

        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinFilterAll, DisplayName = "전체" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.Customer.ToString(), DisplayName = "거래처" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.CustomerContract.ToString(), DisplayName = "계약서" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.Item.ToString(), DisplayName = "품목" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.CompanyProfile.ToString(), DisplayName = "회사설정" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.CustomerCategory.ToString(), DisplayName = "고객분류" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.PriceGradeOption.ToString(), DisplayName = "가격등급" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.TradeTypeOption.ToString(), DisplayName = "거래구분" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.ItemCategoryOption.ToString(), DisplayName = "품목분류" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.Invoice.ToString(), DisplayName = "전표" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.Payment.ToString(), DisplayName = "수금/지급" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.Transaction.ToString(), DisplayName = "거래내역" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.InventoryTransfer.ToString(), DisplayName = "재고이동" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.RentalManagementCompany.ToString(), DisplayName = "렌탈 관리업체" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.RentalBillingProfile.ToString(), DisplayName = "렌탈 청구프로필" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.RentalAsset.ToString(), DisplayName = "렌탈 자산" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.RentalBillingLog.ToString(), DisplayName = "렌탈 청구로그" });

        SelectedRecycleBinTypeOption = RecycleBinTypeOptions[0];
    }

    [RelayCommand]
    private async Task ReloadRecycleBinAsync()
    {
        try
        {
            if (!CanManageRecycleBinData)
            {
                ClearRecycleBinEntriesForPermissionDenied();
                StatusMessage = "휴지통 조회/복원 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
                return;
            }

            IsBusy = true;
            DetachRecycleBinEntryHandlers(_allRecycleBinEntries);
            _allRecycleBinEntries = await _local.GetRecycleBinEntriesAsync(_session);
            AttachRecycleBinEntryHandlers(_allRecycleBinEntries);
            RefreshRecycleBinSummary();
            ApplyRecycleBinFilter();
            StatusMessage = _allRecycleBinEntries.Count == 0
                ? "휴지통이 비어 있습니다."
                : $"휴지통 {RecycleBinTotalCount:N0}건을 불러왔습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RestoreSelectedRecycleBinEntryAsync()
    {
        if (!CanManageRecycleBinData)
        {
            StatusMessage = "휴지통 복원 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            return;
        }

        if (SelectedRecycleBinEntry is null)
        {
            StatusMessage = "복원할 휴지통 항목을 선택하세요.";
            return;
        }

        var confirm = MessageBox.Show(
            $"선택한 '{SelectedRecycleBinEntry.Title}' 항목을 복원하시겠습니까?",
            "휴지통 복원",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        await RestoreRecycleBinEntriesCoreAsync([SelectedRecycleBinEntry]);
    }

    [RelayCommand]
    private async Task RestoreMarkedRecycleBinEntriesAsync()
    {
        if (!CanManageRecycleBinData)
        {
            StatusMessage = "휴지통 복원 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            return;
        }

        var markedEntries = GetMarkedRecycleBinEntries();
        if (markedEntries.Count == 0)
        {
            StatusMessage = "복원할 항목을 먼저 체크하세요.";
            return;
        }

        var confirm = MessageBox.Show(
            $"체크한 휴지통 항목 {markedEntries.Count:N0}건을 복원하시겠습니까?",
            "휴지통 일괄 복원",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        await RestoreRecycleBinEntriesCoreAsync(markedEntries);
    }

    [RelayCommand]
    private async Task PermanentlyDeleteSelectedRecycleBinEntryAsync()
    {
        if (!CanManageRecycleBinData)
        {
            StatusMessage = "휴지통 영구삭제 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            return;
        }

        if (SelectedRecycleBinEntry is null)
        {
            StatusMessage = "영구삭제할 휴지통 항목을 선택하세요.";
            return;
        }

        var confirm = MessageBox.Show(
            $"선택한 '{SelectedRecycleBinEntry.Title}' 항목을 영구삭제하시겠습니까?\n이 작업은 되돌릴 수 없습니다.",
            "휴지통 영구삭제",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        await PermanentlyDeleteRecycleBinEntriesCoreAsync([SelectedRecycleBinEntry]);
    }

    [RelayCommand]
    private async Task PermanentlyDeleteMarkedRecycleBinEntriesAsync()
    {
        if (!CanManageRecycleBinData)
        {
            StatusMessage = "휴지통 영구삭제 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            return;
        }

        var markedEntries = GetMarkedRecycleBinEntries();
        if (markedEntries.Count == 0)
        {
            StatusMessage = "영구삭제할 항목을 먼저 체크하세요.";
            return;
        }

        var confirm = MessageBox.Show(
            $"체크한 휴지통 항목 {markedEntries.Count:N0}건을 영구삭제하시겠습니까?\n이 작업은 되돌릴 수 없습니다.",
            "휴지통 일괄 영구삭제",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        await PermanentlyDeleteRecycleBinEntriesCoreAsync(markedEntries);
    }

    [RelayCommand]
    private async Task MergeSelectedRecycleBinCustomerAsync()
    {
        if (!CanManageRecycleBinData)
        {
            StatusMessage = "휴지통 거래처 병합 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            return;
        }

        if (SelectedRecycleBinEntry?.Kind != RecycleBinEntityKind.Customer)
        {
            StatusMessage = "병합할 삭제 거래처를 먼저 선택하세요.";
            return;
        }

        if (SelectedRecycleBinMergeTarget is null)
        {
            StatusMessage = "연결을 옮길 활성 거래처를 먼저 선택하세요.";
            return;
        }

        var confirm = MessageBox.Show(
            $"삭제된 거래처 '{SelectedRecycleBinEntry.Title}'의 연결 데이터를 '{SelectedRecycleBinMergeTarget.Name}'로 이동한 뒤 영구삭제하시겠습니까?",
            "중복 거래처 정리",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            IsBusy = true;

            if (_session.IsOfflineMode)
            {
                StatusMessage = "오프라인 모드에서는 중복 거래처 정리를 진행할 수 없습니다. 로그인 후 다시 시도하세요.";
                return;
            }

            var mergeResult = await _local.MergeDeletedCustomerIntoAsync(
                SelectedRecycleBinEntry.EntityId,
                SelectedRecycleBinMergeTarget.CustomerId,
                _session);
            if (!mergeResult.Success)
            {
                StatusMessage = mergeResult.Message;
                return;
            }

            await _sync.TrySyncAsync();
            var hasPendingChanges = await _local.HasPendingSyncChangesAsync(_session);

            await ReloadRecycleBinAsync();

            var refreshed = _allRecycleBinEntries.FirstOrDefault(entry =>
                entry.Kind == RecycleBinEntityKind.Customer &&
                entry.EntityId == SelectedRecycleBinEntry.EntityId);
            if (refreshed is null)
            {
                StatusMessage = $"{mergeResult.Message} 삭제본 거래처가 더 이상 휴지통에 없어 정리를 완료했습니다.";
                return;
            }

            SelectedRecycleBinEntry = refreshed;
            if (hasPendingChanges)
            {
                StatusMessage = $"{mergeResult.Message} 서버 반영 대기 데이터가 남아 있어 영구삭제는 보류했습니다.";
                return;
            }

            await PermanentlyDeleteRecycleBinEntriesCoreAsync([refreshed]);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void MarkAllFilteredRecycleBinEntries()
    {
        if (!CanManageRecycleBinData)
        {
            StatusMessage = "휴지통 선택 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            return;
        }

        foreach (var entry in RecycleBinEntries)
            entry.IsMarked = true;

        RefreshMarkedRecycleBinCount();
        StatusMessage = RecycleBinEntries.Count == 0
            ? "현재 필터에 표시된 항목이 없습니다."
            : $"현재 필터의 {RecycleBinEntries.Count:N0}건을 선택했습니다.";
    }

    [RelayCommand]
    private void ClearRecycleBinMarks()
    {
        if (!CanManageRecycleBinData)
        {
            StatusMessage = "휴지통 선택 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            return;
        }

        foreach (var entry in _allRecycleBinEntries)
            entry.IsMarked = false;

        RefreshMarkedRecycleBinCount();
        StatusMessage = "휴지통 선택 표시를 해제했습니다.";
    }

    private async Task RestoreRecycleBinEntriesCoreAsync(IReadOnlyList<RecycleBinEntry> entries)
    {
        try
        {
            IsBusy = true;

            var orderedEntries = entries
                .DistinctBy(entry => (entry.Kind, entry.EntityId))
                .ToList();

            if (_session.IsOfflineMode)
            {
                StatusMessage = "오프라인 모드에서는 휴지통 복원을 진행할 수 없습니다. 로그인 후 다시 시도하세요.";
                return;
            }

            var serverMirror = await MirrorRecycleBinMutationToServerAsync("복원", orderedEntries);
            var localApply = await ApplyConfirmedServerRestoresLocallyAsync(
                serverMirror.SucceededEntries,
                serverMirror.RequiresAuthoritativeRefresh,
                entry => _local.RestoreRecycleBinEntryAsync(entry.Kind, entry.EntityId, _session),
                entry => _local.MarkRecycleBinServerMutationCleanAsync(entry.Kind, entry.EntityId),
                () => _sync.TryAuthoritativePullOnlyAsync(),
                ReloadRecycleBinAsync);
            var succeeded = localApply.SucceededCount;
            var failures = localApply.Failures.ToList();
            failures.AddRange(serverMirror.Failures);

            var mutationStatus = BuildRecycleBinMutationStatusMessage("복원", orderedEntries.Count, succeeded, failures);
            if (localApply.HasLocalApplyFailure)
            {
                StatusMessage =
                    "서버 복원은 확정됐지만 로컬 반영에 실패했습니다. 같은 복원을 반복하지 마세요. " +
                    (serverMirror.HasAmbiguousRestoreOutcome
                        ? "일부 다른 항목의 서버 복원 결과도 불확실합니다. "
                        : string.Empty) +
                    (localApply.AuthoritativeRefreshSucceeded
                        ? "서버 최신 상태를 다시 불러왔습니다. "
                        : $"{localApply.AuthoritativeRefreshFailure ?? "서버 최신 상태를 확인하지 못했습니다."} ") +
                    mutationStatus;
            }
            else if (serverMirror.HasAmbiguousRestoreOutcome)
            {
                StatusMessage =
                    "서버 복원 결과가 불확실합니다. 같은 복원을 반복하지 마세요. " +
                    (localApply.AuthoritativeRefreshSucceeded
                        ? "서버 최신 상태를 다시 불러왔습니다. "
                        : $"{localApply.AuthoritativeRefreshFailure ?? "서버 최신 상태를 확인하지 못했습니다."} ") +
                    mutationStatus;
            }
            else if (localApply.RequiresAuthoritativeRefresh)
            {
                StatusMessage =
                    (localApply.AuthoritativeRefreshSucceeded
                        ? "복원 충돌 후 서버 최신 상태를 다시 불러왔습니다. "
                        : $"{localApply.AuthoritativeRefreshFailure ?? "복원 충돌 후 서버 최신 상태를 확인하지 못했습니다."} ") +
                    mutationStatus;
            }
            else
            {
                StatusMessage = mutationStatus;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal static async Task<RecycleBinConfirmedRestoreLocalApplyResult>
        ApplyConfirmedServerRestoresLocallyAsync(
            IReadOnlyList<RecycleBinEntry> entries,
            bool serverRequiresAuthoritativeRefresh,
            Func<RecycleBinEntry, Task<OfficeMutationResult>> restoreAsync,
            Func<RecycleBinEntry, Task> markCleanAsync,
            Func<Task<bool>> authoritativeRefreshAsync,
            Func<Task> reloadAsync)
    {
        var succeeded = 0;
        var failures = new List<string>();
        var hasLocalApplyFailure = false;
        foreach (var entry in entries)
        {
            OfficeMutationResult result;
            try
            {
                result = await restoreAsync(entry);
            }
            catch (Exception ex)
            {
                hasLocalApplyFailure = true;
                failures.Add(
                    $"{entry.KindText} · {entry.Title}: 서버 복원 확정 후 로컬 복원 반영 실패 - " +
                    (ex.InnerException?.Message ?? ex.Message));
                continue;
            }

            if (!result.Success)
            {
                hasLocalApplyFailure = true;
                failures.Add(
                    $"{entry.KindText} · {entry.Title}: 서버 복원은 확정됐지만 로컬 복원이 거부됐습니다. {result.Message}");
                continue;
            }

            try
            {
                await markCleanAsync(entry);
                succeeded++;
            }
            catch (Exception ex)
            {
                hasLocalApplyFailure = true;
                failures.Add(
                    $"{entry.KindText} · {entry.Title}: 서버 복원 확정 후 로컬 clean 반영 실패 - " +
                    (ex.InnerException?.Message ?? ex.Message));
            }
        }

        var requiresAuthoritativeRefresh =
            serverRequiresAuthoritativeRefresh || hasLocalApplyFailure;
        var authoritativeSyncSucceeded = false;
        string? authoritativeRefreshFailure = null;
        if (requiresAuthoritativeRefresh)
        {
            try
            {
                authoritativeSyncSucceeded = await authoritativeRefreshAsync();
                if (!authoritativeSyncSucceeded)
                    authoritativeRefreshFailure = "서버 최신 상태 동기화에 실패했습니다.";
            }
            catch (Exception ex)
            {
                authoritativeRefreshFailure =
                    $"서버 최신 상태 동기화 실패 - {ex.InnerException?.Message ?? ex.Message}";
            }
        }

        var reloadSucceeded = false;
        try
        {
            await reloadAsync();
            reloadSucceeded = true;
        }
        catch (Exception ex) when (requiresAuthoritativeRefresh)
        {
            authoritativeRefreshFailure =
                $"서버 최신 상태 다시 불러오기 실패 - {ex.InnerException?.Message ?? ex.Message}";
        }

        return new RecycleBinConfirmedRestoreLocalApplyResult(
            succeeded,
            failures,
            hasLocalApplyFailure,
            requiresAuthoritativeRefresh,
            requiresAuthoritativeRefresh && authoritativeSyncSucceeded && reloadSucceeded,
            authoritativeRefreshFailure);
    }

    private async Task PermanentlyDeleteRecycleBinEntriesCoreAsync(IReadOnlyList<RecycleBinEntry> entries)
    {
        try
        {
            IsBusy = true;

            var orderedEntries = entries
                .DistinctBy(entry => (entry.Kind, entry.EntityId))
                .OrderBy(entry => GetRecycleBinPurgeOrder(entry.Kind))
                .ThenByDescending(entry => entry.DeletedAtUtc)
                .ToList();

            if (_session.IsOfflineMode)
            {
                StatusMessage = "오프라인 모드에서는 휴지통 영구삭제를 진행할 수 없습니다. 로그인 후 다시 시도하세요.";
                return;
            }

            var serverMirror = await MirrorRecycleBinMutationToServerAsync("영구삭제", orderedEntries);
            var actionEntries =
                OrderSuccessfulPurgeEntriesForLocalApply(
                    serverMirror.SucceededEntries);
            var failures = new List<string>();
            var locallySuccessfulPurges =
                new List<
                    RecycleBinSuccessfulLocalPurge>();
            var locallyCoveredEntries =
                new HashSet<(
                    RecycleBinEntityKind Kind,
                    Guid EntityId)>();
            foreach (var entry in actionEntries)
            {
                if (locallyCoveredEntries.Contains(
                        (entry.Kind, entry.EntityId)))
                {
                    continue;
                }

                try
                {
                    OfficeMutationResult result;
                    LocalStateService
                            .ServerPurgeConfirmationFence?
                        confirmationFence = null;
                    if (entry.Kind == RecycleBinEntityKind.Invoice)
                    {
                        if (!serverMirror.PurgeConfirmationFences.TryGetValue(
                                (entry.EntityId, entry.Kind),
                                out confirmationFence))
                        {
                            failures.Add(
                                $"{entry.KindText} · {entry.Title}: 서버 요청 전 로컬 삭제 범위를 확인하지 못해 반영을 보류했습니다.");
                            continue;
                        }

                        result =
                            await _local.ApplyConfirmedServerPurgeRecycleBinEntryAsync(
                                entry.Kind,
                                entry.EntityId,
                                entry.Revision,
                                ResolveRecycleBinMutationDatabaseName(entry),
                                confirmationFence);
                    }
                    else
                    {
                        result =
                            await _local.ApplyServerPurgeRecycleBinEntryAsync(
                                entry.Kind,
                                entry.EntityId,
                                entry.Revision,
                                ResolveRecycleBinMutationDatabaseName(
                                    entry));
                    }

                    if (result.Success)
                    {
                        locallySuccessfulPurges.Add(
                            new RecycleBinSuccessfulLocalPurge(
                                entry,
                                confirmationFence));
                        var currentReconciliation =
                            ReconcileSuccessfulPurgeCascades(
                                orderedEntries,
                                locallySuccessfulPurges,
                                [],
                                new Dictionary<
                                    (
                                        RecycleBinEntityKind
                                            Kind,
                                        Guid EntityId),
                                    List<string>>());
                        locallyCoveredEntries.UnionWith(
                            currentReconciliation
                                .CoveredEntries);
                    }
                    else
                    {
                        failures.Add($"{entry.KindText} · {entry.Title}: {result.Message}");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{entry.KindText} · {entry.Title}: 로컬 영구삭제 반영 실패 - {ex.InnerException?.Message ?? ex.Message}");
                }
            }

            var reconciliation =
                ReconcileSuccessfulPurgeCascades(
                    orderedEntries,
                    locallySuccessfulPurges,
                    serverMirror.Failures,
                    serverMirror.EntryFailures);
            failures.AddRange(
                reconciliation
                    .RemainingServerFailures);

            await ReloadRecycleBinAsync();
            StatusMessage = BuildRecycleBinMutationStatusMessage(
                "영구삭제",
                orderedEntries.Count,
                reconciliation.SucceededCount,
                failures);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static IReadOnlyList<RecycleBinEntry>
        OrderSuccessfulPurgeEntriesForLocalApply(
            IEnumerable<RecycleBinEntry> entries)
        => entries
            .OrderBy(entry =>
                entry.Kind == RecycleBinEntityKind.Invoice
                    ? 0
                    : 1)
            .ThenBy(entry =>
                GetRecycleBinPurgeOrder(entry.Kind))
            .ThenByDescending(entry =>
                entry.DeletedAtUtc)
            .ToList();

    private static bool IsEntryCoveredByInvoicePurgeFence(
        RecycleBinEntry entry,
        LocalStateService.ServerPurgeConfirmationFence fence)
    {
        var entityType = entry.Kind switch
        {
            RecycleBinEntityKind.Invoice =>
                "LocalInvoice",
            RecycleBinEntityKind.Payment =>
                "LocalPayment",
            RecycleBinEntityKind.Transaction =>
                "LocalTransaction",
            _ => string.Empty
        };
        return !string.IsNullOrWhiteSpace(entityType) &&
               fence.Entities.Any(current =>
                   current.EntityId == entry.EntityId &&
                   string.Equals(
                       current.EntityType,
                       entityType,
                       StringComparison.Ordinal));
    }

    internal static RecycleBinPurgeCascadeReconciliation
        ReconcileSuccessfulPurgeCascades(
            IReadOnlyList<RecycleBinEntry> selectedEntries,
            IReadOnlyList<RecycleBinSuccessfulLocalPurge>
                locallySuccessfulPurges,
            IReadOnlyList<string> serverFailures,
            IReadOnlyDictionary<
                (RecycleBinEntityKind Kind, Guid EntityId),
                List<string>> serverEntryFailures)
    {
        var selectedKeys = selectedEntries
            .Select(entry =>
                (entry.Kind, entry.EntityId))
            .ToHashSet();
        var coveredEntries =
            new HashSet<(
                RecycleBinEntityKind Kind,
                Guid EntityId)>();
        foreach (var successful in
                 locallySuccessfulPurges)
        {
            var successfulKey = (
                successful.Entry.Kind,
                successful.Entry.EntityId);
            if (selectedKeys.Contains(successfulKey))
                coveredEntries.Add(successfulKey);

            if (successful.ConfirmationFence is not null)
            {
                foreach (var selected in selectedEntries
                             .Where(current =>
                                 IsEntryCoveredByInvoicePurgeFence(
                                     current,
                                     successful
                                         .ConfirmationFence)))
                {
                    coveredEntries.Add(
                        (selected.Kind,
                            selected.EntityId));
                }
            }

            var cascadeKind =
                successful.Entry.Kind switch
                {
                    RecycleBinEntityKind.Payment =>
                        RecycleBinEntityKind.Transaction,
                    RecycleBinEntityKind.Transaction =>
                        RecycleBinEntityKind.Payment,
                    _ => (RecycleBinEntityKind?)null
                };
            if (cascadeKind.HasValue)
            {
                var cascadeKey = (
                    cascadeKind.Value,
                    successful.Entry.EntityId);
                if (selectedKeys.Contains(cascadeKey))
                    coveredEntries.Add(cascadeKey);
            }
        }

        var remainingServerFailures =
            serverFailures.ToList();
        foreach (var coveredEntry in coveredEntries)
        {
            if (!serverEntryFailures.TryGetValue(
                    coveredEntry,
                    out var coveredFailureMessages))
            {
                continue;
            }

            foreach (var coveredFailureMessage in
                     coveredFailureMessages)
            {
                remainingServerFailures.Remove(
                    coveredFailureMessage);
            }
        }

        return new RecycleBinPurgeCascadeReconciliation(
            coveredEntries,
            remainingServerFailures);
    }

    private void ApplyRecycleBinFilter()
    {
        var selectedType = SelectedRecycleBinTypeOption?.Value ?? RecycleBinFilterAll;
        var searchText = RecycleBinSearchText?.Trim() ?? string.Empty;
        var previousSelection = SelectedRecycleBinEntry is null
            ? (Kind: (RecycleBinEntityKind?)null, Id: (Guid?)null)
            : (SelectedRecycleBinEntry.Kind, (Guid?)SelectedRecycleBinEntry.EntityId);

        var filtered = _allRecycleBinEntries
            .Where(entry => string.Equals(selectedType, RecycleBinFilterAll, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(entry.Kind.ToString(), selectedType, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            filtered = filtered.Where(entry =>
                entry.KindText.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                entry.Title.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                entry.Subtitle.Contains(searchText, StringComparison.CurrentCultureIgnoreCase) ||
                entry.Detail.Contains(searchText, StringComparison.CurrentCultureIgnoreCase));
        }

        var items = filtered
            .OrderByDescending(entry => entry.DeletedAtUtc)
            .ThenBy(entry => entry.KindText, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        RecycleBinEntries.Clear();
        foreach (var entry in items)
            RecycleBinEntries.Add(entry);

        SelectedRecycleBinEntry = previousSelection.Id.HasValue
            ? RecycleBinEntries.FirstOrDefault(entry => entry.Kind == previousSelection.Kind && entry.EntityId == previousSelection.Id.Value)
                ?? RecycleBinEntries.FirstOrDefault()
            : RecycleBinEntries.FirstOrDefault();

        RefreshMarkedRecycleBinCount();
        OnPropertyChanged(nameof(HasRecycleBinEntries));
        OnPropertyChanged(nameof(CanMarkFilteredRecycleBinEntries));
    }

    private void ClearRecycleBinEntriesForPermissionDenied()
    {
        DetachRecycleBinEntryHandlers(_allRecycleBinEntries);
        _allRecycleBinEntries = new List<RecycleBinEntry>();
        RecycleBinEntries.Clear();
        SelectedRecycleBinEntry = null;
        RefreshRecycleBinSummary();
        RefreshMarkedRecycleBinCount();
        OnPropertyChanged(nameof(HasRecycleBinEntries));
        OnPropertyChanged(nameof(CanMarkFilteredRecycleBinEntries));
    }

    private void RefreshRecycleBinSummary()
    {
        RecycleBinTotalCount = _allRecycleBinEntries.Count;
        RecycleBinCustomerCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.Customer);
        RecycleBinContractCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.CustomerContract);
        RecycleBinItemCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.Item);
        RecycleBinCompanyProfileCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.CompanyProfile);
        RecycleBinCustomerCategoryCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.CustomerCategory);
        RecycleBinPriceGradeOptionCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.PriceGradeOption);
        RecycleBinTradeTypeOptionCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.TradeTypeOption);
        RecycleBinItemCategoryOptionCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.ItemCategoryOption);
        RecycleBinInvoiceCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.Invoice);
        RecycleBinPaymentCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.Payment);
        RecycleBinTransactionCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.Transaction);
        RecycleBinInventoryTransferCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.InventoryTransfer);
        RecycleBinRentalManagementCompanyCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.RentalManagementCompany);
        RecycleBinRentalBillingProfileCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.RentalBillingProfile);
        RecycleBinRentalAssetCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.RentalAsset);
        RecycleBinRentalBillingLogCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.RentalBillingLog);

        RecycleBinSummary = RecycleBinTotalCount == 0
            ? "삭제된 항목이 없습니다."
            : $"거래처 {RecycleBinCustomerCount:N0} · 계약서 {RecycleBinContractCount:N0} · 품목 {RecycleBinItemCount:N0} · 회사설정 {RecycleBinCompanyProfileCount:N0} · 고객분류 {RecycleBinCustomerCategoryCount:N0} · 가격등급 {RecycleBinPriceGradeOptionCount:N0} · 거래구분 {RecycleBinTradeTypeOptionCount:N0} · 품목분류 {RecycleBinItemCategoryOptionCount:N0} · 전표 {RecycleBinInvoiceCount:N0} · 수금/지급 {RecycleBinPaymentCount:N0} · 거래내역 {RecycleBinTransactionCount:N0} · 재고이동 {RecycleBinInventoryTransferCount:N0} · 렌탈 관리업체 {RecycleBinRentalManagementCompanyCount:N0} · 렌탈청구 {RecycleBinRentalBillingProfileCount:N0} · 렌탈자산 {RecycleBinRentalAssetCount:N0} · 렌탈로그 {RecycleBinRentalBillingLogCount:N0}";
    }

    private void RefreshMarkedRecycleBinCount()
        => MarkedRecycleBinCount = _allRecycleBinEntries.Count(entry => entry.IsMarked);

    private List<RecycleBinEntry> GetMarkedRecycleBinEntries()
        => _allRecycleBinEntries.Where(entry => entry.IsMarked).ToList();

    private void AttachRecycleBinEntryHandlers(IEnumerable<RecycleBinEntry> entries)
    {
        foreach (var entry in entries)
            entry.PropertyChanged += HandleRecycleBinEntryPropertyChanged;
    }

    private void DetachRecycleBinEntryHandlers(IEnumerable<RecycleBinEntry> entries)
    {
        foreach (var entry in entries)
            entry.PropertyChanged -= HandleRecycleBinEntryPropertyChanged;
    }

    private void HandleRecycleBinEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecycleBinEntry.IsMarked))
            RefreshMarkedRecycleBinCount();
    }

    private static int GetRecycleBinPurgeOrder(RecycleBinEntityKind kind)
    {
        return kind switch
        {
            RecycleBinEntityKind.Payment => 0,
            RecycleBinEntityKind.Transaction => 1,
            RecycleBinEntityKind.RentalBillingLog => 2,
            RecycleBinEntityKind.CustomerContract => 3,
            RecycleBinEntityKind.Invoice => 4,
            RecycleBinEntityKind.RentalAsset => 5,
            RecycleBinEntityKind.Item => 6,
            RecycleBinEntityKind.InventoryTransfer => 7,
            RecycleBinEntityKind.CustomerCategory => 8,
            RecycleBinEntityKind.PriceGradeOption => 9,
            RecycleBinEntityKind.TradeTypeOption => 10,
            RecycleBinEntityKind.ItemCategoryOption => 11,
            RecycleBinEntityKind.CompanyProfile => 12,
            RecycleBinEntityKind.RentalManagementCompany => 13,
            RecycleBinEntityKind.RentalBillingProfile => 14,
            RecycleBinEntityKind.Customer => 15,
            _ => 99
        };
    }

    private async Task LoadSelectedRecycleBinContextAsync(RecycleBinEntry? entry)
    {
        SelectedRecycleBinDependencies.Clear();
        SelectedRecycleBinMergeCandidates.Clear();
        SelectedRecycleBinMergeTarget = null;
        SelectedRecycleBinDependencySummary = entry is null
            ? "삭제 차단 사유를 확인하려면 항목을 선택하세요."
            : "삭제 차단 사유를 확인하는 중입니다.";
        OnPropertyChanged(nameof(HasSelectedRecycleBinDependencies));
        OnPropertyChanged(nameof(HasSelectedRecycleBinMergeCandidates));
        OnPropertyChanged(nameof(CanMergeSelectedRecycleBinCustomer));

        if (entry is null)
            return;

        try
        {
            IsRecycleBinDependencyBusy = true;

            var dependencyInfo = await _local.GetRecycleBinDependencyInfoAsync(entry.Kind, entry.EntityId, _session);
            SelectedRecycleBinDependencySummary = dependencyInfo.Summary;
            foreach (var dependency in dependencyInfo.Dependencies)
                SelectedRecycleBinDependencies.Add(dependency);

            if (entry.Kind == RecycleBinEntityKind.Customer)
            {
                var candidates = await _local.GetRecycleBinCustomerMergeCandidatesAsync(entry.EntityId, _session);
                foreach (var candidate in candidates)
                    SelectedRecycleBinMergeCandidates.Add(candidate);
                SelectedRecycleBinMergeTarget = SelectedRecycleBinMergeCandidates.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            SelectedRecycleBinDependencySummary = $"삭제 차단 사유를 불러오지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsRecycleBinDependencyBusy = false;
            OnPropertyChanged(nameof(HasSelectedRecycleBinDependencies));
            OnPropertyChanged(nameof(HasSelectedRecycleBinMergeCandidates));
            OnPropertyChanged(nameof(CanMergeSelectedRecycleBinCustomer));
        }
    }

    private async Task<RecycleBinMirrorResult> MirrorRecycleBinMutationToServerAsync(
        string action,
        IReadOnlyList<RecycleBinEntry> entries)
    {
        var mirrorResult = new RecycleBinMirrorResult();
        if (entries.Count == 0)
            return mirrorResult;
        if (_session.IsOfflineMode)
        {
            mirrorResult.Failures.Add($"오프라인 모드에서는 휴지통 {action}를 서버에 반영할 수 없습니다.");
            return mirrorResult;
        }

        var targetEntries = entries
            .Select(entry => new { Entry = entry, Target = ToServerRecycleBinTarget(entry) })
            .ToList();
        var unsupportedEntries = targetEntries
            .Where(current => current.Target is null)
            .Select(current => current.Entry)
            .ToList();
        foreach (var unsupported in unsupportedEntries)
            mirrorResult.Failures.Add($"{unsupported.KindText} · {unsupported.Title}: 서버 휴지통 연동 대상이 아닙니다.");

        var targets = targetEntries
            .Where(current => current.Target is not null)
            .ToDictionary(
                current => (current.Entry.EntityId, NormalizeServerRecycleBinKind(current.Target!.Kind)),
                current => new
                {
                    current.Entry,
                    Target = current.Target!
                });
        if (targets.Count == 0)
            return mirrorResult;

        var remainingTargets = targets.Values.ToList();
        if (string.Equals(
                action,
                "영구삭제",
                StringComparison.Ordinal))
        {
            foreach (var current in remainingTargets
                         .Where(current =>
                             current.Entry.Kind ==
                             RecycleBinEntityKind.Invoice)
                         .ToList())
            {
                try
                {
                    var confirmationFence =
                        await _local
                            .CaptureServerPurgeConfirmationFenceAsync(
                                current.Entry.Kind,
                                current.Entry.EntityId,
                                ResolveRecycleBinMutationDatabaseName(
                                    current.Entry));
                    if (confirmationFence is null)
                    {
                        mirrorResult.Failures.Add(
                            $"{current.Entry.KindText} · {current.Entry.Title}: 서버 요청 전 로컬 삭제 범위를 확인하지 못했습니다.");
                        remainingTargets.Remove(current);
                        continue;
                    }

                    mirrorResult.PurgeConfirmationFences[
                        (current.Entry.EntityId,
                            current.Entry.Kind)] =
                        confirmationFence;
                }
                catch (Exception ex)
                {
                    mirrorResult.Failures.Add(
                        $"{current.Entry.KindText} · {current.Entry.Title}: 서버 요청 전 로컬 삭제 범위 확인 실패 - {ex.InnerException?.Message ?? ex.Message}");
                    remainingTargets.Remove(current);
                }
            }
        }

        var groupedTargets = remainingTargets
            .GroupBy(current => ResolveRecycleBinMutationDatabaseName(current.Entry), StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var targetGroup in groupedTargets)
        {
            var businessDatabaseName = targetGroup.Key;
            var groupTargets = targetGroup.ToList();
            foreach (var batch in groupTargets.Chunk(RecycleBinServerMutationBatchSize))
            {
                var batchTargets = batch.ToDictionary(
                    current => (current.Entry.EntityId, NormalizeServerRecycleBinKind(current.Target.Kind)),
                    current => (current.Entry, current.Target));

                try
                {
                    var mutationTargets = batchTargets.Values
                        .Select(current => current.Target)
                        .ToList();
                    var result = string.Equals(action, "복원", StringComparison.Ordinal)
                        ? await _api.RestoreRecycleBinAsync(mutationTargets, businessDatabaseName)
                        : await _api.PurgeRecycleBinAsync(mutationTargets, businessDatabaseName);

                    ApplyRecycleBinServerMutationBatchResult(action, batchTargets, result, mirrorResult);
                }
                catch (Exception ex)
                {
                    if (string.Equals(action, "복원", StringComparison.Ordinal))
                    {
                        mirrorResult.RequiresAuthoritativeRefresh = true;
                        mirrorResult.HasAmbiguousRestoreOutcome |=
                            ex is AmbiguousMutationOutcomeException or
                            HttpRequestException { StatusCode: null };
                    }

                    mirrorResult.Failures.Add(
                        $"Linux PC 서버 {action} 반영 실패({FormatRecycleBinMutationDatabaseLabel(businessDatabaseName)}): {ex.Message}" +
                        (groupTargets.Count > batch.Length
                            ? " 일부 남은 항목 처리는 중단했습니다."
                            : string.Empty));
                    break;
                }
            }
        }

        return mirrorResult;
    }

    private string ResolveRecycleBinMutationDatabaseName(RecycleBinEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.BusinessDatabaseName))
            return TenantScopeCatalog.GetDatabaseName(entry.BusinessDatabaseName);
        if (!string.IsNullOrWhiteSpace(_session.SelectedBusinessDatabaseName))
            return TenantScopeCatalog.GetDatabaseName(_session.SelectedBusinessDatabaseName);
        return TenantScopeCatalog.GetDatabaseName(_session.TenantCode);
    }

    private static string FormatRecycleBinMutationDatabaseLabel(string businessDatabaseName)
    {
        var databaseName = TenantScopeCatalog.GetDatabaseName(businessDatabaseName);
        var displayName = TenantScopeCatalog.GetBusinessDatabaseDisplayName(databaseName);
        return TenantScopeCatalog.FormatBusinessDatabaseLabel(displayName, databaseName);
    }

    private void ApplyRecycleBinServerMutationBatchResult(
        string action,
        IReadOnlyDictionary<(Guid EntityId, string Kind), (RecycleBinEntry Entry, RecycleBinMutationTargetDto Target)> batchTargets,
        RecycleBinMutationResultDto? result,
        RecycleBinMirrorResult mirrorResult)
    {
        if (result is null)
        {
            if (string.Equals(action, "복원", StringComparison.Ordinal))
            {
                mirrorResult.RequiresAuthoritativeRefresh = true;
                mirrorResult.HasAmbiguousRestoreOutcome = true;
            }

            mirrorResult.Failures.Add($"Linux PC 서버 {action} 반영 결과를 확인하지 못했습니다.");
            return;
        }

        if (result.Results.Count == 0)
        {
            if (result.SucceededCount >= batchTargets.Count)
                mirrorResult.SucceededEntries.AddRange(batchTargets.Values.Select(current => current.Entry));
            else
            {
                if (string.Equals(action, "복원", StringComparison.Ordinal))
                    mirrorResult.RequiresAuthoritativeRefresh = true;
                mirrorResult.Failures.Add(result.Messages.FirstOrDefault()
                                          ?? $"Linux PC 서버 {action} 반영 중 실패한 항목이 있습니다.");
            }
            return;
        }

        var reported = new HashSet<(Guid EntityId, string Kind)>();
        foreach (var itemResult in result.Results)
        {
            var key = (itemResult.EntityId, NormalizeServerRecycleBinKind(itemResult.Kind));
            if (!batchTargets.TryGetValue(key, out var target))
            {
                if (!itemResult.Success && !string.IsNullOrWhiteSpace(itemResult.Message))
                    mirrorResult.Failures.Add(itemResult.Message);
                continue;
            }

            reported.Add(key);
            if (itemResult.Success)
                mirrorResult.SucceededEntries.Add(target.Entry);
            else
            {
                if (string.Equals(action, "복원", StringComparison.Ordinal))
                    mirrorResult.RequiresAuthoritativeRefresh = true;
                mirrorResult.AddEntryFailure(
                    target.Entry,
                    $"{target.Entry.KindText} · {target.Entry.Title}: {itemResult.Message}");
            }
        }

        foreach (var key in batchTargets.Keys.Where(key => !reported.Contains(key)))
        {
            var target = batchTargets[key];
            if (string.Equals(action, "복원", StringComparison.Ordinal))
            {
                mirrorResult.RequiresAuthoritativeRefresh = true;
                mirrorResult.HasAmbiguousRestoreOutcome = true;
            }
            mirrorResult.Failures.Add($"{target.Entry.KindText} · {target.Entry.Title}: Linux PC 서버 {action} 결과를 확인하지 못했습니다.");
        }
    }

    private static RecycleBinMutationTargetDto? ToServerRecycleBinTarget(RecycleBinEntry entry)
    {
        var kind = entry.Kind switch
        {
            RecycleBinEntityKind.Customer => "customer",
            RecycleBinEntityKind.CustomerContract => "contract",
            RecycleBinEntityKind.Item => "item",
            RecycleBinEntityKind.CompanyProfile => "company-profile",
            RecycleBinEntityKind.CustomerCategory => "customer-category",
            RecycleBinEntityKind.PriceGradeOption => "price-grade-option",
            RecycleBinEntityKind.TradeTypeOption => "trade-type-option",
            RecycleBinEntityKind.ItemCategoryOption => "item-category-option",
            RecycleBinEntityKind.Invoice => "invoice",
            RecycleBinEntityKind.Payment => "payment",
            RecycleBinEntityKind.Transaction => "transaction",
            RecycleBinEntityKind.InventoryTransfer => "inventory-transfer",
            RecycleBinEntityKind.RentalManagementCompany => "rental-management-company",
            RecycleBinEntityKind.RentalBillingProfile => "rental-billing-profile",
            RecycleBinEntityKind.RentalAsset => "rental-asset",
            RecycleBinEntityKind.RentalBillingLog => "rental-billing-log",
            _ => string.Empty
        };

        return string.IsNullOrWhiteSpace(kind)
            ? null
            : new RecycleBinMutationTargetDto
            {
                EntityId = entry.EntityId,
                Kind = kind,
                ExpectedRevision = entry.Revision
            };
    }

    private static string NormalizeServerRecycleBinKind(string? kind)
        => (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "customer" => "customer",
            "contract" => "contract",
            "item" => "item",
            "companyprofile" => "company-profile",
            "company-profile" => "company-profile",
            "customercategory" => "customer-category",
            "customer-category" => "customer-category",
            "pricegradeoption" => "price-grade-option",
            "price-grade-option" => "price-grade-option",
            "tradetypeoption" => "trade-type-option",
            "trade-type-option" => "trade-type-option",
            "itemcategoryoption" => "item-category-option",
            "item-category-option" => "item-category-option",
            "invoice" => "invoice",
            "payment" => "payment",
            "transaction" => "transaction",
            "inventorytransfer" => "inventory-transfer",
            "inventory-transfer" => "inventory-transfer",
            "rentalmanagementcompany" => "rental-management-company",
            "rental-management-company" => "rental-management-company",
            "rentalbillingprofile" => "rental-billing-profile",
            "rental-billing-profile" => "rental-billing-profile",
            "rentalasset" => "rental-asset",
            "rental-asset" => "rental-asset",
            "rentalbillinglog" => "rental-billing-log",
            "rental-billing-log" => "rental-billing-log",
            _ => string.Empty
        };

    private static string BuildRecycleBinMutationStatusMessage(
        string action,
        int requestedCount,
        int succeededCount,
        IReadOnlyList<string> failures)
    {
        if (requestedCount == 0)
            return $"{action}할 항목이 없습니다.";
        if (failures.Count == 0)
            return $"휴지통 항목 {succeededCount:N0}건을 {action}했습니다.";

        var failedCount = requestedCount - succeededCount;
        return $"휴지통 {action} 완료: 성공 {succeededCount:N0}건 / 실패 {failedCount:N0}건. {failures[0]}";
    }
}
