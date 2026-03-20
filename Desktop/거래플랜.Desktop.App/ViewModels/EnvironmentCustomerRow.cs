using CommunityToolkit.Mvvm.ComponentModel;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class EnvironmentCustomerRow : ObservableObject
{
    private string _savedOfficeCode;
    private readonly CustomerContractSummaryItem? _contractSummary;

    public LocalCustomer Source { get; }
    public Guid Id => Source.Id;
    public string NameOriginal => Source.NameOriginal;
    public string CategoryName { get; }
    public string TradeType => CustomerTradeTypes.Normalize(Source.TradeType);
    public string BusinessNumber => Source.BusinessNumber;
    public string Phone => Source.Phone;
    public int ContractCount => _contractSummary?.ContractCount ?? 0;
    public bool HasContract => ContractCount > 0;
    public DateOnly? NearestExpireDate => _contractSummary?.NearestExpireDate;
    public string ContractPresenceText => HasContract ? $"{ContractCount}건" : "-";
    public string ContractStatusText => _contractSummary switch
    {
        null => "없음",
        { ContractCount: <= 0 } => "없음",
        { HasExpiredContract: true } => "만료 계약 있음",
        { ExpiringSoonCount: > 0 } summary => $"{summary.ExpiringSoonCount}건 임박",
        { NearestExpireDate: not null } summary => $"{summary.NearestExpireDate:yyyy-MM-dd}",
        _ => "등록됨"
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModified))]
    private string _responsibleOfficeCode;

    public bool IsModified => !string.Equals(
        NormalizeOfficeCode(ResponsibleOfficeCode),
        _savedOfficeCode,
        StringComparison.OrdinalIgnoreCase);

    public EnvironmentCustomerRow(
        LocalCustomer source,
        string? categoryName = null,
        CustomerContractSummaryItem? contractSummary = null)
    {
        Source = source;
        _contractSummary = contractSummary;
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
        => OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode, DomainConstants.OfficeUsenet);
}

