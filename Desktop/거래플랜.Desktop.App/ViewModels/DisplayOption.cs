namespace 거래플랜.Desktop.App.ViewModels;

public sealed class DisplayOption
{
    public string Value { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;

    public override string ToString() => DisplayName;
}
