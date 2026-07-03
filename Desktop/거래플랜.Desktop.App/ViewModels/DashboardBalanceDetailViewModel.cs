using System.Collections.ObjectModel;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed class DashboardBalanceDetailViewModel
{
    public DashboardBalanceDetailViewModel(
        string title,
        string subtitle,
        string balanceKindText,
        string accentBrush,
        IReadOnlyList<DashboardBalanceDetailRow> rows)
    {
        Title = title;
        Subtitle = subtitle;
        BalanceKindText = balanceKindText;
        AccentBrush = accentBrush;
        Rows = new ObservableCollection<DashboardBalanceDetailRow>(rows);
        TotalAmount = rows.Sum(row => row.BalanceAmount);
        CustomerCount = rows.Select(row => row.CustomerId).Distinct().Count();
        InvoiceCount = rows.Count;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string BalanceKindText { get; }
    public string AccentBrush { get; }
    public ObservableCollection<DashboardBalanceDetailRow> Rows { get; }
    public decimal TotalAmount { get; }
    public int CustomerCount { get; }
    public int InvoiceCount { get; }
    public bool HasRows => Rows.Count > 0;
    public string TotalAmountText => $"{TotalAmount:N0}원";
    public string CustomerCountText => $"{CustomerCount:N0}곳";
    public string InvoiceCountText => $"{InvoiceCount:N0}건";
    public string SummaryText => HasRows
        ? $"{BalanceKindText} {TotalAmount:N0}원 · 거래처 {CustomerCount:N0}곳 · 전표 {InvoiceCount:N0}건"
        : $"{BalanceKindText}이 남은 전표가 없습니다.";
}

public sealed class DashboardBalanceDetailRow
{
    public Guid CustomerId { get; init; }
    public string CustomerName { get; init; } = string.Empty;
    public decimal CustomerBalance { get; init; }
    public Guid InvoiceId { get; init; }
    public string InvoiceNumberDisplay { get; init; } = string.Empty;
    public DateOnly InvoiceDate { get; init; }
    public string InvoiceDateDisplay => InvoiceDate.ToString("yyyy/MM/dd");
    public VoucherType VoucherType { get; init; }
    public string VoucherTypeDisplay => VoucherType switch
    {
        VoucherType.Sales => "매출",
        VoucherType.Purchase => "매입",
        VoucherType.Procurement => "발주",
        VoucherType.Expense => "경비",
        VoucherType.Collection => "수금",
        _ => VoucherType.ToString()
    };
    public string FirstItemSummary { get; init; } = string.Empty;
    public decimal TotalAmount { get; init; }
    public decimal SettledAmount { get; init; }
    public decimal BalanceAmount { get; init; }
    public string ResponsibleOfficeCode { get; init; } = string.Empty;
}
