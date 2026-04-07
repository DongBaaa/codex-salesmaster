using CommunityToolkit.Mvvm.ComponentModel;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class SelectableFilterOption : ObservableObject
{
    public SelectableFilterOption(string value, string displayName, bool isSelected = true)
    {
        Value = value;
        DisplayName = displayName;
        _isSelected = isSelected;
    }

    public string Value { get; }

    public string DisplayName { get; }

    [ObservableProperty] private bool _isSelected;
}
