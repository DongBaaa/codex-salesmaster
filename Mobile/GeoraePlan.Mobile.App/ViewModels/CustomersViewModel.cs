using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class CustomersViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;

    private string _searchText = string.Empty;
    private string _statusMessage = "거래처를 불러오세요.";
    private bool _isBusy;

    public CustomersViewModel(GeoraePlanApiClient api)
    {
        _api = api;
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public ObservableCollection<CustomerDto> Customers { get; } = new();

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

    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "거래처 조회 중...";
            var result = await _api.GetCustomersAsync(SearchText);
            Customers.Clear();
            foreach (var customer in result)
                Customers.Add(customer);

            StatusMessage = $"거래처 {Customers.Count}건";
        }
        catch (Exception ex)
        {
            StatusMessage = $"거래처 조회 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
