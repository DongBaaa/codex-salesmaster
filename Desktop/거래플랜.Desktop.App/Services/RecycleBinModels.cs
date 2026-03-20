using CommunityToolkit.Mvvm.ComponentModel;

namespace 거래플랜.Desktop.App.Services;

public enum RecycleBinEntityKind
{
    Customer,
    CustomerContract,
    Item,
    Invoice,
    Payment,
    Transaction
}

public sealed partial class RecycleBinEntry : ObservableObject
{
    public Guid EntityId { get; init; }
    public RecycleBinEntityKind Kind { get; init; }
    [ObservableProperty] private bool _isMarked;

    public string KindText => Kind switch
    {
        RecycleBinEntityKind.Customer => "거래처",
        RecycleBinEntityKind.CustomerContract => "계약서",
        RecycleBinEntityKind.Item => "품목",
        RecycleBinEntityKind.Invoice => "전표",
        RecycleBinEntityKind.Payment => "수금/지급",
        RecycleBinEntityKind.Transaction => "거래내역",
        _ => "휴지통"
    };

    public string Title { get; init; } = string.Empty;
    public string Subtitle { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public DateTime DeletedAtUtc { get; init; }
    public string DeletedAtLocalText => DeletedAtUtc == default
        ? "-"
        : DeletedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}
