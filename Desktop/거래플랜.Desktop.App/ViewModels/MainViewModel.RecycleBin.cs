using CommunityToolkit.Mvvm.ComponentModel;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class MainViewModel
{
    [ObservableProperty] private int _dashboardRecycleBinCount;

    public bool HasDashboardRecycleBinItems => DashboardRecycleBinCount > 0;
    public string DashboardRecycleBinBadgeText => DashboardRecycleBinCount > 0
        ? $"휴지통 {DashboardRecycleBinCount:N0}"
        : "휴지통";

    partial void OnDashboardRecycleBinCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasDashboardRecycleBinItems));
        OnPropertyChanged(nameof(DashboardRecycleBinBadgeText));
    }

    private async Task RefreshRecycleBinDashboardAsync(CancellationToken ct = default)
    {
        var recycleBinCount = (await _local.GetRecycleBinEntriesAsync(_session, ct)).Count;
        DashboardRecycleBinCount = recycleBinCount;
    }
}
