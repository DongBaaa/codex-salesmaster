namespace GeoraePlan.Mobile.App.Services;

public sealed class MobileRefreshCoordinator
{
    private int _customersVersion;
    private int _itemsVersion;
    private int _invoicesVersion;

    public void MarkCustomersChanged() => Interlocked.Increment(ref _customersVersion);
    public void MarkItemsChanged() => Interlocked.Increment(ref _itemsVersion);
    public void MarkInvoicesChanged() => Interlocked.Increment(ref _invoicesVersion);

    public int CustomersVersion => Volatile.Read(ref _customersVersion);
    public int ItemsVersion => Volatile.Read(ref _itemsVersion);
    public int InvoicesVersion => Volatile.Read(ref _invoicesVersion);
}
