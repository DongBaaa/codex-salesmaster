using CommunityToolkit.Mvvm.ComponentModel;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class EnvironmentCustomerRow : ObservableObject
{
    private string _savedOfficeCode;

    public LocalCustomer Source { get; }
    public Guid Id => Source.Id;
    public string NameOriginal => Source.NameOriginal;
    public string CategoryName { get; }
    public string TradeType => CustomerTradeTypes.Normalize(Source.TradeType);
    public string BusinessNumber => Source.BusinessNumber;
    public string Phone => Source.Phone;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private string _responsibleOfficeCode;

    public bool IsModified => !string.Equals(
        NormalizeOfficeCode(ResponsibleOfficeCode),
        _savedOfficeCode,
        StringComparison.OrdinalIgnoreCase);

    public EnvironmentCustomerRow(LocalCustomer source, string? categoryName = null)
    {
        Source = source;
        CategoryName = string.IsNullOrWhiteSpace(categoryName) ? "-" : categoryName.Trim();
        _savedOfficeCode = NormalizeOfficeCode(source.ResponsibleOfficeCode);
        _responsibleOfficeCode = _savedOfficeCode;
    }

    public void ApplyToSource()
    {
        Source.ResponsibleOfficeCode = NormalizeOfficeCode(ResponsibleOfficeCode);
    }

    public void AcceptChanges()
    {
        _savedOfficeCode = NormalizeOfficeCode(ResponsibleOfficeCode);
        Source.ResponsibleOfficeCode = _savedOfficeCode;
        OnPropertyChanged(nameof(IsModified));
    }

    private static string NormalizeOfficeCode(string? officeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet);
}

