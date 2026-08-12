namespace 거래플랜.Desktop.App.Services;

public sealed class RentalStateChangedEventArgs : EventArgs
{
    public RentalStateChangedEventArgs(
        IEnumerable<Guid>? assetIds,
        IEnumerable<Guid>? billingProfileIds,
        string reason,
        object? origin = null)
    {
        AssetIds = NormalizeIds(assetIds);
        BillingProfileIds = NormalizeIds(billingProfileIds);
        Reason = (reason ?? string.Empty).Trim();
        Origin = origin;
    }

    public IReadOnlyList<Guid> AssetIds { get; }
    public IReadOnlyList<Guid> BillingProfileIds { get; }
    public string Reason { get; }
    public object? Origin { get; }
    public bool HasRentalChanges => AssetIds.Count > 0 || BillingProfileIds.Count > 0;

    private static IReadOnlyList<Guid> NormalizeIds(IEnumerable<Guid>? ids)
        => (ids ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
}
