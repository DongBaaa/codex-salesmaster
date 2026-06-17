using CommunityToolkit.Mvvm.ComponentModel;

namespace 거래플랜.Desktop.App.Services;

public enum RecycleBinEntityKind
{
    Customer,
    CustomerContract,
    Item,
    CompanyProfile,
    CustomerCategory,
    PriceGradeOption,
    TradeTypeOption,
    ItemCategoryOption,
    Invoice,
    Payment,
    Transaction,
    InventoryTransfer,
    RentalManagementCompany,
    RentalBillingProfile,
    RentalAsset,
    RentalBillingLog
}

public sealed partial class RecycleBinEntry : ObservableObject
{
    public Guid EntityId { get; init; }
    public RecycleBinEntityKind Kind { get; init; }
    [ObservableProperty] private bool _isMarked;
    public string TenantCode { get; init; } = string.Empty;
    public string OfficeCode { get; init; } = string.Empty;
    public string ResponsibleOfficeCode { get; init; } = string.Empty;
    public string ManagementCompanyCode { get; init; } = string.Empty;
    public string BusinessDatabaseName { get; init; } = string.Empty;

    public string KindText => Kind switch
    {
        RecycleBinEntityKind.Customer => "거래처",
        RecycleBinEntityKind.CustomerContract => "계약서",
        RecycleBinEntityKind.Item => "품목",
        RecycleBinEntityKind.CompanyProfile => "회사설정",
        RecycleBinEntityKind.CustomerCategory => "고객분류",
        RecycleBinEntityKind.PriceGradeOption => "가격등급",
        RecycleBinEntityKind.TradeTypeOption => "거래구분",
        RecycleBinEntityKind.ItemCategoryOption => "품목분류",
        RecycleBinEntityKind.Invoice => "전표",
        RecycleBinEntityKind.Payment => "수금/지급",
        RecycleBinEntityKind.Transaction => "거래내역",
        RecycleBinEntityKind.InventoryTransfer => "재고이동",
        RecycleBinEntityKind.RentalManagementCompany => "렌탈 관리업체",
        RecycleBinEntityKind.RentalBillingProfile => "렌탈 청구프로필",
        RecycleBinEntityKind.RentalAsset => "렌탈 자산",
        RecycleBinEntityKind.RentalBillingLog => "렌탈 청구로그",
        _ => "휴지통"
    };

    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public DateTime DeletedAtUtc { get; init; }
    public long Revision { get; init; }
    public string DeletedAtLocalText => DeletedAtUtc == default
        ? "-"
        : DeletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed class RecycleBinDependencyInfo
{
    public bool CanPurge { get; init; }
    public string Summary { get; init; } = string.Empty;
    public List<RecycleBinDependencyItem> Dependencies { get; init; } = new();
}

public sealed class RecycleBinDependencyItem
{
    public string Label { get; init; } = string.Empty;
    public int Count { get; init; }
    public string Detail { get; init; } = string.Empty;
    public string DisplayText => Count > 0
        ? $"{Label} {Count:N0}건"
        : Label;
}

public sealed class RecycleBinCustomerMergeCandidate
{
    public Guid CustomerId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string BusinessNumber { get; init; } = string.Empty;
    public string Phone { get; init; } = string.Empty;
    public string ResponsibleOfficeCode { get; init; } = string.Empty;
    public string DisplayText
        => string.Join(" / ",
            new[]
            {
                Name,
                string.IsNullOrWhiteSpace(BusinessNumber) ? null : BusinessNumber,
                string.IsNullOrWhiteSpace(Phone) ? null : Phone,
                string.IsNullOrWhiteSpace(ResponsibleOfficeCode) ? null : ResponsibleOfficeCode
            }.Where(segment => !string.IsNullOrWhiteSpace(segment)));
}
