using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using Microsoft.Maui.ApplicationModel;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class CustomerContractsViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;
    private readonly CustomerContractCacheStore _cacheStore;

    private Guid _customerId;
    private string _customerName = "거래처 계약서";
    private string _statusMessage = "거래처 계약서를 불러오세요.";
    private bool _isBusy;

    public CustomerContractsViewModel(GeoraePlanApiClient api, CustomerContractCacheStore cacheStore)
    {
        _api = api;
        _cacheStore = cacheStore;
        RefreshCommand = new AsyncCommand(RefreshAsync);
    }

    public ObservableCollection<CustomerContractDto> Contracts { get; } = new();

    public string CustomerName
    {
        get => _customerName;
        set => SetProperty(ref _customerName, value);
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

    public async Task InitializeAsync(Guid customerId, string customerName)
    {
        _customerId = customerId;
        CustomerName = string.IsNullOrWhiteSpace(customerName) ? "거래처 계약서" : customerName;
        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (IsBusy || _customerId == Guid.Empty)
            return;

        try
        {
            IsBusy = true;
            StatusMessage = "계약서를 서버에서 조회하고 있습니다.";
            var result = await _api.GetCustomerContractsAsync(_customerId);
            await _cacheStore.SaveContractsAsync(_customerId, result);
            ReplaceContracts(result);

            StatusMessage = result.Count == 0
                ? "등록된 계약서가 없습니다."
                : $"계약서 {result.Count:N0}건을 불러왔습니다.";
        }
        catch (Exception ex)
        {
            var cachedContracts = await _cacheStore.LoadContractsAsync(_customerId);
            ReplaceContracts(cachedContracts);

            StatusMessage = cachedContracts.Count == 0
                ? $"계약서 조회 실패: {ex.Message}"
                : $"서버 연결에 실패해 캐시 계약서 {cachedContracts.Count:N0}건을 표시합니다. ({ex.Message})";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task OpenContractAsync(CustomerContractDto? contract)
    {
        if (contract is null)
        {
            StatusMessage = "열 계약서를 선택하세요.";
            return;
        }

        try
        {
            var path = await _cacheStore.EnsureCachedPdfAsync(_customerId, contract);
            if (string.IsNullOrWhiteSpace(path))
            {
                var downloadedPath = await _api.DownloadCustomerContractAsync(contract);
                path = await _cacheStore.CachePdfAsync(_customerId, contract.Id, downloadedPath);
            }

            await Launcher.Default.OpenAsync(new OpenFileRequest(contract.FileName, new ReadOnlyFile(path)));
            StatusMessage = "계약서 PDF를 열었습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"계약서 열기 실패: {ex.Message}";
        }
    }

    private void ReplaceContracts(IReadOnlyList<CustomerContractDto> contracts)
    {
        Contracts.Clear();
        foreach (var contract in contracts)
            Contracts.Add(contract);
    }
}
