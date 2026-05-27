namespace GeoraePlan.Mobile.App.Services;

public sealed class MobileRefreshCoordinator
{
    private int _customersVersion;
    private int _itemsVersion;
    private int _invoicesVersion;
    private int _rentalsVersion;
    private int _inventoryTransfersVersion;

    public event EventHandler? AllChanged;

    public void MarkCustomersChanged() => Interlocked.Increment(ref _customersVersion);
    public void MarkItemsChanged() => Interlocked.Increment(ref _itemsVersion);
    public void MarkInvoicesChanged() => Interlocked.Increment(ref _invoicesVersion);
    public void MarkRentalsChanged() => Interlocked.Increment(ref _rentalsVersion);
    public void MarkInventoryTransfersChanged() => Interlocked.Increment(ref _inventoryTransfersVersion);
    public void MarkAllChanged()
    {
        MarkCustomersChanged();
        MarkItemsChanged();
        MarkInvoicesChanged();
        MarkRentalsChanged();
        MarkInventoryTransfersChanged();
        AllChanged?.Invoke(this, EventArgs.Empty);
    }

    public int CustomersVersion => Volatile.Read(ref _customersVersion);
    public int ItemsVersion => Volatile.Read(ref _itemsVersion);
    public int InvoicesVersion => Volatile.Read(ref _invoicesVersion);
    public int RentalsVersion => Volatile.Read(ref _rentalsVersion);
    public int InventoryTransfersVersion => Volatile.Read(ref _inventoryTransfersVersion);
}
