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
    [ObservableProperty] private int _markedRecycleBinCount;
    [ObservableProperty] private string _recycleBinSummary = "휴지통이 비어 있습니다.";

    public ObservableCollection<DisplayOption> RecycleBinTypeOptions { get; } = new();
    public ObservableCollection<RecycleBinEntry> RecycleBinEntries { get; } = new();

    public bool HasRecycleBinEntries => RecycleBinEntries.Count > 0;
    public bool HasSelectedRecycleBinEntry => SelectedRecycleBinEntry is not null;
    public bool HasMarkedRecycleBinEntries => MarkedRecycleBinCount > 0;

    partial void OnSelectedRecycleBinTypeOptionChanged(DisplayOption? value) => ApplyRecycleBinFilter();
    partial void OnRecycleBinSearchTextChanged(string value) => ApplyRecycleBinFilter();
    partial void OnSelectedRecycleBinEntryChanged(RecycleBinEntry? value) => OnPropertyChanged(nameof(HasSelectedRecycleBinEntry));
    partial void OnMarkedRecycleBinCountChanged(int value) => OnPropertyChanged(nameof(HasMarkedRecycleBinEntries));

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

            var succeeded = 0;
            var succeededEntries = new List<RecycleBinEntry>();
            var failures = new List<string>();
            foreach (var entry in entries.DistinctBy(entry => (entry.Kind, entry.EntityId)))
            {
                var result = await _local.RestoreRecycleBinEntryAsync(entry.Kind, entry.EntityId, _session);
                if (result.Success)
                {
                    succeeded++;
                    succeededEntries.Add(entry);
                }
                else
                    failures.Add($"{entry.KindText} · {entry.Title}: {result.Message}");
            }

            var serverMessage = await MirrorRecycleBinMutationToServerAsync("복원", succeededEntries);
            await ReloadRecycleBinAsync();
            StatusMessage = BuildRecycleBinMutationStatusMessage("복원", entries.Count, succeeded, failures);
            if (!string.IsNullOrWhiteSpace(serverMessage))
                StatusMessage = $"{StatusMessage} / {serverMessage}";
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

            var succeeded = 0;
            var succeededEntries = new List<RecycleBinEntry>();
            var failures = new List<string>();
            foreach (var entry in orderedEntries)
            {
                var result = await _local.PermanentlyDeleteRecycleBinEntryAsync(entry.Kind, entry.EntityId, _session);
                if (result.Success)
                {
                    succeeded++;
                    succeededEntries.Add(entry);
                }
                else
                    failures.Add($"{entry.KindText} · {entry.Title}: {result.Message}");
            }

            var serverMessage = await MirrorRecycleBinMutationToServerAsync("영구삭제", succeededEntries);
            await ReloadRecycleBinAsync();
            StatusMessage = BuildRecycleBinMutationStatusMessage("영구삭제", orderedEntries.Count, succeeded, failures);
            if (!string.IsNullOrWhiteSpace(serverMessage))
                StatusMessage = $"{StatusMessage} / {serverMessage}";
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

        RecycleBinSummary = RecycleBinTotalCount == 0
            ? "삭제된 항목이 없습니다."
            : $"거래처 {RecycleBinCustomerCount:N0} · 계약서 {RecycleBinContractCount:N0} · 품목 {RecycleBinItemCount:N0} · 전표 {RecycleBinInvoiceCount:N0} · 수금/지급 {RecycleBinPaymentCount:N0} · 거래내역 {RecycleBinTransactionCount:N0}";
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
            RecycleBinEntityKind.CustomerContract => 2,
            RecycleBinEntityKind.Invoice => 3,
            RecycleBinEntityKind.Item => 4,
            RecycleBinEntityKind.Customer => 5,
            _ => 99
        };
    }

    private async Task<string?> MirrorRecycleBinMutationToServerAsync(
        string action,
        IReadOnlyList<RecycleBinEntry> entries)
    {
        if (_session.IsOfflineMode || entries.Count == 0)
            return null;

        var targets = entries
            .Select(ToServerRecycleBinTarget)
            .Where(target => target is not null)
            .Cast<RecycleBinMutationTargetDto>()
            .ToList();
        if (targets.Count == 0)
            return null;

        try
        {
            var result = string.Equals(action, "복원", StringComparison.Ordinal)
                ? await _api.RestoreRecycleBinAsync(targets)
                : await _api.PurgeRecycleBinAsync(targets);

            if (result is null || result.SucceededCount >= result.RequestedCount)
                return null;

            var failedCount = result.RequestedCount - result.SucceededCount;
            return result.Messages.FirstOrDefault()
                   ?? $"NAS 서버 {action} 반영 중 {failedCount:N0}건이 실패했습니다.";
        }
        catch (Exception ex)
        {
            return $"NAS 서버 {action} 반영 실패: {ex.Message}";
        }
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
