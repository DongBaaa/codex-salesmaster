using CommunityToolkit.Mvvm.ComponentModel;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class AttachmentSelectionItemViewModel : ObservableObject
{
    public AttachmentSelectionItemViewModel(
        string code,
        string displayName,
        bool isToggleEnabled)
    {
        Code = code;
        DisplayName = displayName;
        IsToggleEnabled = isToggleEnabled;
    }

    public string Code { get; }
    public string DisplayName { get; }
    public bool IsToggleEnabled { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrderDisplay))]
    private int? _orderIndex;

    [ObservableProperty]
    private bool _isChecked;

    public string OrderDisplay => OrderIndex.HasValue ? $"{OrderIndex.Value}." : string.Empty;

    public event Action<AttachmentSelectionItemViewModel, bool>? CheckedChanged;

    partial void OnIsCheckedChanged(bool value)
    {
        CheckedChanged?.Invoke(this, value);
    }
}
