namespace GeoraePlan.Mobile.App.Models;

public sealed class RecentItemSelectionRecord
{
    public Guid ItemId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string ItemNameOriginal { get; set; } = string.Empty;
    public string SpecificationOriginal { get; set; } = string.Empty;
    public string MaterialNumber { get; set; } = string.Empty;
    public DateTime SelectedAtUtc { get; set; } = DateTime.UtcNow;

    public string DisplayName
        => string.IsNullOrWhiteSpace(SpecificationOriginal)
            ? string.IsNullOrWhiteSpace(MaterialNumber)
                ? ItemNameOriginal
                : $"{ItemNameOriginal} · 자재 {MaterialNumber}"
            : string.IsNullOrWhiteSpace(MaterialNumber)
                ? $"{ItemNameOriginal} · {SpecificationOriginal}"
                : $"{ItemNameOriginal} · {SpecificationOriginal} · 자재 {MaterialNumber}";

    public string SecondaryText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(SpecificationOriginal))
                parts.Add(SpecificationOriginal.Trim());
            if (!string.IsNullOrWhiteSpace(MaterialNumber))
                parts.Add($"자재 {MaterialNumber.Trim()}");
            if (parts.Count == 0 && !string.IsNullOrWhiteSpace(CategoryName))
                parts.Add(CategoryName.Trim());

            return parts.Count == 0 ? "최근 선택 품목" : string.Join(" · ", parts);
        }
    }
}
