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
    public int RegisteredFileCount => _contractSummary?.RegisteredFileCount ?? 0;
    public int DraftContractCount => _contractSummary?.DraftCount ?? 0;
    public bool HasContract => RegisteredFileCount > 0;
    public DateOnly? NearestExpireDate => _contractSummary?.NearestExpireDate;
    public string ContractPresenceText => ContractCount == 0 ? "-"
        : RegisteredFileCount == 0 ? $"초안 {DraftContractCount}건"
        : DraftContractCount == 0 ? $"PDF {RegisteredFileCount}건"
        : $"PDF {RegisteredFileCount}건 · 초안 {DraftContractCount}건";
    public string ContractStatusText
    {
        get
        {
            if (_contractSummary is null || ContractCount <= 0)
                return "없음";
            var status = _contractSummary switch
            {
                { HasExpiredContract: true } => "만료 계약 있음",
                { ExpiringSoonCount: > 0 } summary => $"{summary.ExpiringSoonCount}건 임박",
                { NearestExpireDate: not null } summary => $"{summary.NearestExpireDate:yyyy-MM-dd}",
                _ => string.Empty
            };
            if (_contractSummary.MissingExpireDateCount > 0)
                return string.IsNullOrEmpty(status) ? "만료일 미입력"
                    : $"{status} · 만료일 미입력 {_contractSummary.MissingExpireDateCount}건";
            return status;
        }
    }

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

    public void RestoreSavedOfficeCode()
    {
        ResponsibleOfficeCode = _savedOfficeCode;
        Source.ResponsibleOfficeCode = _savedOfficeCode;
        OnPropertyChanged(nameof(IsModified));
    }

    private static string NormalizeOfficeCode(string? officeCode)
    {
        if (string.IsNullOrWhiteSpace(officeCode))
            return string.Empty;

        return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet);
    }
}
