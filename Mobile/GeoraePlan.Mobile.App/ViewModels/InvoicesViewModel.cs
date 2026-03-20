using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class InvoicesViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;

    private string _searchText = string.Empty;
    private string _statusMessage = "전표를 불러오세요.";
    private bool _isBusy;
    private DateTime? _lastRefreshUtc;

    public InvoicesViewModel(GeoraePlanApiClient api)
    {
        _api = api;
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public ObservableCollection<InvoiceDto> Invoices { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public AsyncCommand RefreshCommand { get; }

    public bool NeedsRefresh(TimeSpan maxAge)
        => !_lastRefreshUtc.HasValue || DateTime.UtcNow - _lastRefreshUtc.Value >= maxAge;

    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "전표를 조회하고 있습니다.";
            var result = await _api.GetInvoicesAsync(SearchText);

            Invoices.Clear();
            foreach (var invoice in result)
                Invoices.Add(invoice);

            _lastRefreshUtc = DateTime.UtcNow;
            StatusMessage = $"전표 {Invoices.Count:N0}건";
        }
        catch (Exception ex)
        {
            StatusMessage = $"전표 조회 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
