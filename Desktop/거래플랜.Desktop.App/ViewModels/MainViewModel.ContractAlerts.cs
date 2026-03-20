using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class MainViewModel
{
    private const int DashboardContractAlertWindowDays = 30;

    [ObservableProperty] private int _dashboardCustomersWithContractsCount;
    [ObservableProperty] private int _dashboardContractExpiredCount;
    [ObservableProperty] private int _dashboardContractExpiringSoonCount;
    [ObservableProperty] private int _dashboardContractAlertCount;
    [ObservableProperty] private string _dashboardContractAlertSummary = "계약서 알림 없음";
    [ObservableProperty] private string _contractAlertPopupMessage = string.Empty;

    public ObservableCollection<CustomerContractAlertItem> DashboardContractAlerts { get; } = new();

    public bool HasDashboardContractAlerts => DashboardContractAlertCount > 0;

    partial void OnDashboardContractAlertCountChanged(int value)
        => OnPropertyChanged(nameof(HasDashboardContractAlerts));

    private async Task RefreshContractDashboardAsync(CancellationToken ct = default)
    {
        var summaryMap = await _local.GetCustomerContractSummaryMapAsync(_session, DashboardContractAlertWindowDays, ct);
        var alerts = await _local.GetCustomerContractAlertsAsync(_session, DashboardContractAlertWindowDays, ct);

        DashboardCustomersWithContractsCount = summaryMap.Values.Count(summary => summary.ContractCount > 0);
        DashboardContractExpiredCount = alerts.Count(alert => alert.DaysRemaining < 0);
        DashboardContractExpiringSoonCount = alerts.Count(alert => alert.DaysRemaining >= 0);
        DashboardContractAlertCount = alerts.Count;

        DashboardContractAlerts.Clear();
        foreach (var alert in alerts.Take(4))
            DashboardContractAlerts.Add(alert);

        DashboardContractAlertSummary = alerts.Count == 0
            ? DashboardCustomersWithContractsCount == 0
                ? "등록된 계약서가 없습니다."
                : $"계약서 보유 거래처 {DashboardCustomersWithContractsCount:N0}곳 · 임박 알림 없음"
            : $"계약서 보유 {DashboardCustomersWithContractsCount:N0}곳 · 만료 {DashboardContractExpiredCount:N0}건 · {DashboardContractAlertWindowDays:N0}일 내 {DashboardContractExpiringSoonCount:N0}건";
        ContractAlertPopupMessage = BuildContractAlertPopupMessage(alerts);
    }

    private static string BuildContractAlertPopupMessage(IReadOnlyList<CustomerContractAlertItem> alerts)
    {
        if (alerts.Count == 0)
            return string.Empty;

        var lines = alerts
            .Take(5)
            .Select(alert => $"• {alert.CustomerName} / {alert.ContractType} / {alert.AlertText}");

        var suffix = alerts.Count > 5
            ? $"{Environment.NewLine}외 {alerts.Count - 5:N0}건이 더 있습니다."
            : string.Empty;

        return $"계약서 만료 알림{Environment.NewLine}{string.Join(Environment.NewLine, lines)}{suffix}";
    }
}
