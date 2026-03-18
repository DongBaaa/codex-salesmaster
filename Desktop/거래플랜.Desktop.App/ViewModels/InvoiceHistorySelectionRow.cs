using CommunityToolkit.Mvvm.ComponentModel;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class InvoiceHistorySelectionRow : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public Guid InvoiceId { get; init; }
    public DateOnly InvoiceDate { get; init; }
    public string InvoiceDateDisplay => InvoiceDate.ToString("yyyy-MM-dd");
    public string InvoiceNumber { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
    public int LineCount { get; init; }
    public string Summary { get; init; } = string.Empty;
    public string Memo { get; init; } = string.Empty;
}
