using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class EnvironmentSettingsViewModel
{
    private const string RecycleBinFilterAll = "ALL";
    private List<RecycleBinEntry> _allRecycleBinEntries = new();

    private sealed class RecycleBinMirrorResult
    {
        public List<RecycleBinEntry> SucceededEntries { get; } = new();
        public List<string> Failures { get; } = new();
    }

    [ObservableProperty] private DisplayOption? _selectedRecycleBinTypeOption;
    [ObservableProperty] private string _recycleBinSearchText = string.Empty;
    [ObservableProperty] private RecycleBinEntry? _selectedRecycleBinEntry;
    [ObservableProperty] private int _recycleBinTotalCount;
    [ObservableProperty] private int _recycleBinCustomerCount;
    [ObservableProperty] private int _recycleBinContractCount;
    [ObservableProperty] private int _recycleBinItemCount;
    [ObservableProperty] private int _recycleBinInvoiceCount;
    [ObservableProperty] private int _recycleBinPaymentCount;
    [ObservableProperty] private int _recycleBinTransactionCount;
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
    public bool CanMergeSelectedRecycleBinCustomer => SelectedRecycleBinEntry?.Kind == RecycleBinEntityKind.Customer &&
                                                      SelectedRecycleBinEntry is not null &&
                                                      SelectedRecycleBinMergeTarget is not null;

    partial void OnSelectedRecycleBinTypeOptionChanged(DisplayOption? value) => ApplyRecycleBinFilter();
    partial void OnRecycleBinSearchTextChanged(string value) => ApplyRecycleBinFilter();
    partial void OnSelectedRecycleBinEntryChanged(RecycleBinEntry? value)
    {
        OnPropertyChanged(nameof(HasSelectedRecycleBinEntry));
        OnPropertyChanged(nameof(CanMergeSelectedRecycleBinCustomer));
        _ = LoadSelectedRecycleBinContextAsync(value);
    }
    partial void OnMarkedRecycleBinCountChanged(int value) => OnPropertyChanged(nameof(HasMarkedRecycleBinEntries));
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
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.Invoice.ToString(), DisplayName = "전표" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.Payment.ToString(), DisplayName = "수금/지급" });
        RecycleBinTypeOptions.Add(new DisplayOption { Value = RecycleBinEntityKind.Transaction.ToString(), DisplayName = "거래내역" });
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

            var syncSucceeded = await _sync.TrySyncAsync();
            var hasPendingChanges = await _local.HasPendingSyncChangesAsync();
            if (syncSucceeded && !hasPendingChanges)
                await _sync.RefreshSharedMirrorFromServerAsync();

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
            var actionEntries = serverMirror.SucceededEntries;
            var succeeded = 0;
            var failures = new List<string>();
            foreach (var entry in actionEntries)
            {
                var result = await _local.RestoreRecycleBinEntryAsync(entry.Kind, entry.EntityId, _session);
                if (result.Success)
                    succeeded++;
                else
                    failures.Add($"{entry.KindText} · {entry.Title}: {result.Message}");
            }

            failures.AddRange(serverMirror.Failures);

            await ReloadRecycleBinAsync();
            StatusMessage = BuildRecycleBinMutationStatusMessage("복원", orderedEntries.Count, succeeded, failures);
        }
        finally
        {
            IsBusy = false;
        }
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
            var actionEntries = serverMirror.SucceededEntries;
            var failures = new List<string>();
            var succeeded = 0;
            foreach (var entry in actionEntries)
            {
                var result = await _local.ApplyServerPurgeRecycleBinEntryAsync(entry.Kind, entry.EntityId);
                if (result.Success)
                    succeeded++;
                else
                    failures.Add($"{entry.KindText} · {entry.Title}: {result.Message}");
            }

            failures.AddRange(serverMirror.Failures);

            await ReloadRecycleBinAsync();
            StatusMessage = BuildRecycleBinMutationStatusMessage("영구삭제", orderedEntries.Count, succeeded, failures);
        }
        finally
        {
            IsBusy = false;
        }
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
    }

    private void RefreshRecycleBinSummary()
    {
        RecycleBinTotalCount = _allRecycleBinEntries.Count;
        RecycleBinCustomerCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.Customer);
        RecycleBinContractCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.CustomerContract);
        RecycleBinItemCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.Item);
        RecycleBinInvoiceCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.Invoice);
        RecycleBinPaymentCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.Payment);
        RecycleBinTransactionCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.Transaction);
        RecycleBinRentalBillingProfileCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.RentalBillingProfile);
        RecycleBinRentalAssetCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.RentalAsset);
        RecycleBinRentalBillingLogCount = _allRecycleBinEntries.Count(entry => entry.Kind == RecycleBinEntityKind.RentalBillingLog);

        RecycleBinSummary = RecycleBinTotalCount == 0
            ? "삭제된 항목이 없습니다."
            : $"거래처 {RecycleBinCustomerCount:N0} · 계약서 {RecycleBinContractCount:N0} · 품목 {RecycleBinItemCount:N0} · 전표 {RecycleBinInvoiceCount:N0} · 수금/지급 {RecycleBinPaymentCount:N0} · 거래내역 {RecycleBinTransactionCount:N0} · 렌탈청구 {RecycleBinRentalBillingProfileCount:N0} · 렌탈자산 {RecycleBinRentalAssetCount:N0} · 렌탈로그 {RecycleBinRentalBillingLogCount:N0}";
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
            RecycleBinEntityKind.RentalBillingProfile => 7,
            RecycleBinEntityKind.Customer => 8,
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

        try
        {
            var result = string.Equals(action, "복원", StringComparison.Ordinal)
                ? await _api.RestoreRecycleBinAsync(targets.Values
                    .Select(current => new RecycleBinMutationTargetDto
                    {
                        EntityId = current.Target.EntityId,
                        Kind = current.Target.Kind
                    })
                    .ToList())
                : await _api.PurgeRecycleBinAsync(targets.Values
                    .Select(current => new RecycleBinMutationTargetDto
                    {
                        EntityId = current.Target.EntityId,
                        Kind = current.Target.Kind
                    })
                    .ToList());

            if (result is null)
            {
                mirrorResult.Failures.Add($"NAS 서버 {action} 반영 결과를 확인하지 못했습니다.");
                return mirrorResult;
            }

            if (result.Results.Count == 0)
            {
                if (result.SucceededCount >= targets.Count)
                    mirrorResult.SucceededEntries.AddRange(targets.Values.Select(current => current.Entry));
                else
                    mirrorResult.Failures.Add(result.Messages.FirstOrDefault()
                                              ?? $"NAS 서버 {action} 반영 중 실패한 항목이 있습니다.");
                return mirrorResult;
            }

            var reported = new HashSet<(Guid EntityId, string Kind)>();
            foreach (var itemResult in result.Results)
            {
                var key = (itemResult.EntityId, NormalizeServerRecycleBinKind(itemResult.Kind));
                if (!targets.TryGetValue(key, out var target))
                {
                    if (!itemResult.Success && !string.IsNullOrWhiteSpace(itemResult.Message))
                        mirrorResult.Failures.Add(itemResult.Message);
                    continue;
                }

                reported.Add(key);
                if (itemResult.Success)
                    mirrorResult.SucceededEntries.Add(target.Entry);
                else
                    mirrorResult.Failures.Add($"{target.Entry.KindText} · {target.Entry.Title}: {itemResult.Message}");
            }

            foreach (var key in targets.Keys.Where(key => !reported.Contains(key)))
            {
                var target = targets[key];
                mirrorResult.Failures.Add($"{target.Entry.KindText} · {target.Entry.Title}: NAS 서버 {action} 결과를 확인하지 못했습니다.");
            }
        }
        catch (Exception ex)
        {
            mirrorResult.Failures.Add($"NAS 서버 {action} 반영 실패: {ex.Message}");
        }

        return mirrorResult;
    }

    private static RecycleBinMutationTargetDto? ToServerRecycleBinTarget(RecycleBinEntry entry)
    {
        var kind = entry.Kind switch
        {
            RecycleBinEntityKind.Customer => "customer",
            RecycleBinEntityKind.CustomerContract => "contract",
            RecycleBinEntityKind.Item => "item",
            RecycleBinEntityKind.Invoice => "invoice",
            RecycleBinEntityKind.Payment => "payment",
            RecycleBinEntityKind.Transaction => "transaction",
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
                Kind = kind
            };
    }

    private static string NormalizeServerRecycleBinKind(string? kind)
        => (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "customer" => "customer",
            "contract" => "contract",
            "item" => "item",
            "invoice" => "invoice",
            "payment" => "payment",
            "transaction" => "transaction",
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

