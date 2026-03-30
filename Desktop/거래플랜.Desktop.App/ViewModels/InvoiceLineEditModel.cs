using CommunityToolkit.Mvvm.ComponentModel;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

/// <summary>
/// Editable row model for invoice line items in the DataGrid.
/// </summary>
public sealed partial class InvoiceLineEditModel : ObservableObject
{
    private bool _suppressLineAmountSync;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ItemId { get; set; }
    public string ItemTrackingType { get; set; } = ItemTrackingTypes.Stock;

    [ObservableProperty]
    private string _itemName = string.Empty;

    [ObservableProperty] private string _specification = string.Empty;
    [ObservableProperty] private string _unit = string.Empty;

    [ObservableProperty]
    private decimal _quantity = 1m;

    [ObservableProperty]
    private decimal _unitPrice;

    [ObservableProperty]
    private decimal _lineAmount;

    [ObservableProperty] private string _remark = string.Empty;
    [ObservableProperty] private string _serialNumber = string.Empty;
    [ObservableProperty] private string _materialNumber = string.Empty;
    [ObservableProperty] private string _installLocation = string.Empty;
    [ObservableProperty] private DateOnly? _rentalStartDate;
    [ObservableProperty] private DateOnly? _rentalEndDate;

    partial void OnQuantityChanged(decimal value) => SyncLineAmountFromInputs();

    partial void OnUnitPriceChanged(decimal value) => SyncLineAmountFromInputs();

    private void SyncLineAmountFromInputs()
    {
        if (_suppressLineAmountSync)
            return;

        _suppressLineAmountSync = true;
        try
        {
            LineAmount = Math.Round(Quantity * UnitPrice, 0, MidpointRounding.AwayFromZero);
        }
        finally
        {
            _suppressLineAmountSync = false;
        }
    }

    public static InvoiceLineEditModel FromLocal(LocalInvoiceLine l) => new()
    {
        Id = l.Id,
        ItemId = l.ItemId,
        ItemName = l.ItemNameOriginal,
        Specification = l.SpecificationOriginal,
        Unit = l.Unit,
        Quantity = l.Quantity,
        UnitPrice = l.UnitPrice,
        LineAmount = l.LineAmount,
        Remark = l.Remark,
        SerialNumber = l.SerialNumber,
        MaterialNumber = l.MaterialNumber,
        InstallLocation = l.InstallLocation,
        RentalStartDate = l.RentalStartDate,
        RentalEndDate = l.RentalEndDate,
        ItemTrackingType = ItemTrackingTypes.Normalize(l.ItemTrackingType)
    };

    public LocalInvoiceLine ToLocal(Guid invoiceId) => new()
    {
        Id = Id,
        InvoiceId = invoiceId,
        ItemId = ItemId,
        ItemNameOriginal = ItemName,
        SpecificationOriginal = Specification,
        Unit = Unit,
        Quantity = Quantity,
        UnitPrice = UnitPrice,
        LineAmount = LineAmount,
        Remark = Remark,
        SerialNumber = SerialNumber,
        MaterialNumber = MaterialNumber,
        InstallLocation = InstallLocation,
        RentalStartDate = RentalStartDate,
        RentalEndDate = RentalEndDate,
        ItemTrackingType = ItemTrackingTypes.Normalize(ItemTrackingType)
    };
}
