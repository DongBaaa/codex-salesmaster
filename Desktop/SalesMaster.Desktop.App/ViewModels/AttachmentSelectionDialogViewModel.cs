using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesMaster.Desktop.App.Printing;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class AttachmentSelectionDialogViewModel : ObservableObject
{
    private readonly string _anchorCode;
    private readonly IReadOnlyList<AttachmentDocumentDefinition> _definitions;
    private readonly HashSet<string> _lockedCodes;
    private readonly List<string> _frontGroup = [];
    private readonly List<string> _backGroup = [];
    private bool _updatingInternally;

    [ObservableProperty]
    private string _statusMessage = "첨부서류를 선택하면 대금청구서를 기준으로 자동 정렬됩니다.";

    [ObservableProperty] private bool _wasConfirmed;

    public ObservableCollection<AttachmentSelectionItemViewModel> Items { get; } = [];
    public event Action<bool?>? RequestClose;

    public AttachmentSelectionDialogViewModel(
        IReadOnlyList<AttachmentDocumentDefinition> definitions,
        IReadOnlyList<AttachmentSelectionState> initialSelections,
        string anchorCode,
        IReadOnlyCollection<string>? lockedCodes = null)
    {
        _definitions = definitions ?? [];
        _anchorCode = anchorCode;
        _lockedCodes = new HashSet<string>(lockedCodes ?? [], StringComparer.OrdinalIgnoreCase);
        _lockedCodes.Add(_anchorCode);

        foreach (var definition in _definitions)
        {
            var item = new AttachmentSelectionItemViewModel(
                definition.Code,
                definition.DisplayName,
                !IsCodeLocked(definition.Code));

            item.CheckedChanged += OnItemCheckedChanged;
            Items.Add(item);
        }

        InitializeSelections(initialSelections ?? []);
    }

    [RelayCommand]
    private void Confirm()
    {
        WasConfirmed = true;
        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel()
    {
        WasConfirmed = false;
        RequestClose?.Invoke(false);
    }

    public IReadOnlyList<AttachmentSelectionState> GetSelectionStates()
    {
        return Items
            .Select(i => new AttachmentSelectionState
            {
                DocCode = i.Code,
                IsChecked = i.IsChecked,
                OrderIndex = i.OrderIndex
            })
            .ToList();
    }

    public IReadOnlyList<AttachmentSelectionState> GetCheckedStatesInOrder()
    {
        return GetSelectionStates()
            .Where(s => s.IsChecked && s.OrderIndex.HasValue)
            .OrderBy(s => s.OrderIndex!.Value)
            .ToList();
    }

    private void InitializeSelections(IReadOnlyList<AttachmentSelectionState> initialSelections)
    {
        _updatingInternally = true;

        var stateByCode = initialSelections
            .Where(s => !string.IsNullOrWhiteSpace(s.DocCode))
            .GroupBy(s => s.DocCode, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var item in Items)
        {
            if (stateByCode.TryGetValue(item.Code, out var state))
                item.IsChecked = state.IsChecked;
        }

        EnsureLockedChecked();

        var orderByCode = stateByCode
            .Where(kv => kv.Value.OrderIndex.HasValue)
            .ToDictionary(kv => kv.Key, kv => kv.Value.OrderIndex!.Value, StringComparer.OrdinalIgnoreCase);

        var definitionOrder = _definitions
            .Select((definition, index) => new { definition.Code, Index = index })
            .ToDictionary(x => x.Code, x => x.Index, StringComparer.OrdinalIgnoreCase);

        var anchorOrder = orderByCode.TryGetValue(_anchorCode, out var order) ? order : int.MaxValue;

        var orderedCheckedCodes = Items
            .Where(i => i.IsChecked)
            .Select(i => i.Code)
            .OrderBy(code => orderByCode.TryGetValue(code, out var idx) ? idx : int.MaxValue)
            .ThenBy(code => definitionOrder.TryGetValue(code, out var idx) ? idx : int.MaxValue)
            .ToList();

        _frontGroup.Clear();
        _backGroup.Clear();

        foreach (var code in orderedCheckedCodes)
        {
            if (string.Equals(code, _anchorCode, StringComparison.OrdinalIgnoreCase))
                continue;

            var hasOrder = orderByCode.TryGetValue(code, out var codeOrder);
            var goesFront =
                IsFrontBaseCode(code) &&
                (!hasOrder || codeOrder < anchorOrder || anchorOrder == int.MaxValue);

            if (goesFront)
                _frontGroup.Add(code);
            else
                _backGroup.Add(code);
        }

        _updatingInternally = false;
        RecalculateOrderIndexes();
    }

    private void OnItemCheckedChanged(AttachmentSelectionItemViewModel item, bool isChecked)
    {
        if (_updatingInternally)
            return;

        if (IsCodeLocked(item.Code))
        {
            if (!isChecked)
            {
                _updatingInternally = true;
                item.IsChecked = true;
                _updatingInternally = false;
            }

            RecalculateOrderIndexes();
            return;
        }

        _frontGroup.RemoveAll(code => string.Equals(code, item.Code, StringComparison.OrdinalIgnoreCase));
        _backGroup.RemoveAll(code => string.Equals(code, item.Code, StringComparison.OrdinalIgnoreCase));

        if (isChecked)
        {
            // New checks are always appended after anchor.
            _backGroup.Add(item.Code);
        }

        RecalculateOrderIndexes();
    }

    private void RecalculateOrderIndexes()
    {
        _updatingInternally = true;

        EnsureLockedChecked();

        NormalizeGroup(_frontGroup);
        NormalizeGroup(_backGroup);

        // Safety: any checked non-anchor not present in groups goes to back.
        foreach (var checkedItem in Items.Where(i => i.IsChecked &&
                                                     !string.Equals(i.Code, _anchorCode, StringComparison.OrdinalIgnoreCase)))
        {
            var inFront = _frontGroup.Any(c => string.Equals(c, checkedItem.Code, StringComparison.OrdinalIgnoreCase));
            var inBack = _backGroup.Any(c => string.Equals(c, checkedItem.Code, StringComparison.OrdinalIgnoreCase));
            if (!inFront && !inBack)
                _backGroup.Add(checkedItem.Code);
        }
        NormalizeGroup(_backGroup);

        foreach (var item in Items)
            item.OrderIndex = null;

        var ordered = new List<string>();
        ordered.AddRange(_frontGroup);
        ordered.Add(_anchorCode);
        ordered.AddRange(_backGroup);

        var order = 1;
        foreach (var code in ordered.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var item = Items.FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase));
            if (item is null || !item.IsChecked)
                continue;

            item.OrderIndex = order++;
        }

        _updatingInternally = false;
    }

    private void EnsureLockedChecked()
    {
        foreach (var item in Items.Where(i => IsCodeLocked(i.Code)))
        {
            if (!item.IsChecked)
                item.IsChecked = true;
        }
    }

    private void NormalizeGroup(List<string> group)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in group)
        {
            if (string.Equals(code, _anchorCode, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!IsCheckedCode(code))
                continue;
            if (!seen.Add(code))
                continue;

            normalized.Add(code);
        }

        group.Clear();
        group.AddRange(normalized);
    }

    private bool IsCheckedCode(string code)
    {
        return Items.FirstOrDefault(i => string.Equals(i.Code, code, StringComparison.OrdinalIgnoreCase))?.IsChecked == true;
    }

    private static bool IsFrontBaseCode(string code)
    {
        return string.Equals(code, AttachmentDocumentCatalog.Statement, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(code, AttachmentDocumentCatalog.Estimate, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCodeLocked(string code)
    {
        return _lockedCodes.Contains(code);
    }
}

