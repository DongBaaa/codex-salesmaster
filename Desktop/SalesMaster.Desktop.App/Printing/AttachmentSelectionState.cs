namespace SalesMaster.Desktop.App.Printing;

public sealed class AttachmentSelectionState
{
    public string DocCode { get; set; } = string.Empty;
    public bool IsChecked { get; set; }
    public int? OrderIndex { get; set; }
}
