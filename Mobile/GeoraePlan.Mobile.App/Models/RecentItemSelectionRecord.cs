namespace GeoraePlan.Mobile.App.Models;

public sealed class RecentItemSelectionRecord
{
    public Guid ItemId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string ItemNameOriginal { get; set; } = string.Empty;
    public string SpecificationOriginal { get; set; } = string.Empty;
    public DateTime SelectedAtUtc { get; set; } = DateTime.UtcNow;

    public string DisplayName
        => string.IsNullOrWhiteSpace(SpecificationOriginal)
            ? ItemNameOriginal
            : $"{ItemNameOriginal} · {SpecificationOriginal}";
}
