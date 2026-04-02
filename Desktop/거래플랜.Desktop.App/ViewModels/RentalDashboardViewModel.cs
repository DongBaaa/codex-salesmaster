using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalDashboardViewModel : ObservableObject
{
    private readonly RentalStateService _rental;
    private readonly SessionState _session;

    [ObservableProperty] private int _dueTodayCount;
    [ObservableProperty] private int _upcomingCount;
    [ObservableProperty] private int _overdueCount;
    [ObservableProperty] private int _activeAssetCount;
    [ObservableProperty] private int _expiringContractCount;
    [ObservableProperty] private int _unassignedCount;
    [ObservableProperty] private int _billingCustomerUnlinkedCount;
    [ObservableProperty] private int _assetCustomerUnlinkedCount;
    [ObservableProperty] private int _assetBillingUnlinkedCount;
    [ObservableProperty] private int _assetlessBillingProfileCount;
    [ObservableProperty] private string _unresolvedLinkSummaryText1 = string.Empty;
    [ObservableProperty] private string _unresolvedLinkSummaryText2 = string.Empty;
    [ObservableProperty] private string _statusMessage = "렌탈 현황을 불러오는 중입니다.";
    [ObservableProperty] private DateOnly _referenceDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private bool _isBusy;

    public ObservableCollection<RentalAlertItem> AlertItems { get; } = new();
    public ObservableCollection<RentalExpiringAssetItem> ExpiringAssets { get; } = new();
    public ObservableCollection<RentalLinkReviewItem> UnresolvedLinkItems { get; } = new();

    public RentalDashboardViewModel(RentalStateService rental, SessionState session)
    {
        _rental = rental;
        _session = session;
    }

    public async Task LoadAsync()
    {
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var summary = await _rental.GetDashboardSummaryAsync(_session, ReferenceDate);
            DueTodayCount = summary.DueTodayCount;
            UpcomingCount = summary.UpcomingCount;
            OverdueCount = summary.OverdueCount;
            ActiveAssetCount = summary.ActiveAssetCount;
            ExpiringContractCount = summary.ExpiringContractCount;
            UnassignedCount = summary.UnassignedCount;
            BillingCustomerUnlinkedCount = summary.BillingCustomerUnlinkedCount;
            AssetCustomerUnlinkedCount = summary.AssetCustomerUnlinkedCount;
            AssetBillingUnlinkedCount = summary.AssetBillingUnlinkedCount;
            AssetlessBillingProfileCount = summary.AssetlessBillingProfileCount;
            UnresolvedLinkSummaryText1 = $"프로필 고객 미연결 {summary.BillingCustomerUnlinkedCount:N0}건 / 자산 고객 미연결 {summary.AssetCustomerUnlinkedCount:N0}건";
            UnresolvedLinkSummaryText2 = $"자산 청구 미연결 {summary.AssetBillingUnlinkedCount:N0}건 / 자산 없는 청구프로필 {summary.AssetlessBillingProfileCount:N0}건";

            AlertItems.Clear();
            foreach (var item in summary.AlertItems)
                AlertItems.Add(item);

            ExpiringAssets.Clear();
            foreach (var asset in summary.ExpiringAssets)
                ExpiringAssets.Add(asset);

            UnresolvedLinkItems.Clear();
            foreach (var item in summary.UnresolvedLinkItems)
                UnresolvedLinkItems.Add(item);

            StatusMessage = summary.AlertItems.Count == 0 && summary.ExpiringAssets.Count == 0 && summary.UnresolvedLinkItems.Count == 0
                ? "알림 대상이 없습니다."
                : $"청구 알림 {summary.AlertItems.Count:N0}건, 만료 예정 {summary.ExpiringAssets.Count:N0}건, 연계 검토 {summary.UnresolvedLinkItems.Count:N0}건";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
