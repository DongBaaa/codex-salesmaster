namespace 거래플랜.Desktop.App.Views;

/// <summary>
/// Generic search result row for the lookup dialog.
/// </summary>
public sealed class LookupRow
{
    public Guid Id { get; init; }
    public string PrimaryText { get; init; } = string.Empty;
    public string SecondaryText { get; init; } = string.Empty;
    public object? Tag { get; init; }
}
