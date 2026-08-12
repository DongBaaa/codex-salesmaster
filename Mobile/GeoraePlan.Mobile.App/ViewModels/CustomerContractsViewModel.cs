using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Services;
using Microsoft.Maui.ApplicationModel;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class CustomerContractsViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;
    private readonly CustomerContractCacheStore _cacheStore;
    private readonly SessionStore _sessionStore;
    private readonly MobileOwnerOperationGate _ownerOperations;

    private Guid _customerId;
    private MobileSessionOwner? _contextOwner;
    private string _customerName = "거래처 계약서";
    private string _statusMessage = "거래처 계약서를 불러오세요.";
    private bool _isBusy;

    public CustomerContractsViewModel(
        GeoraePlanApiClient api,
        CustomerContractCacheStore cacheStore,
        SessionStore sessionStore)
    {
        _api = api;
        _cacheStore = cacheStore;
        _sessionStore = sessionStore;
        _ownerOperations =
            new MobileOwnerOperationGate(sessionStore);
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

    public bool EnsureContextOwnerCurrent()
    {
        var owner = _ownerOperations.EnsureCurrentOwner(
            ResetForOwner);
        IsBusy = _ownerOperations.IsBusy;
        if (IsContextOwner(owner) &&
            _ownerOperations.IsCurrent(owner))
            return true;

        ResetForOwner();
        StatusMessage =
            "로그인 사용자가 변경되었습니다. 이전 화면으로 돌아가 거래처를 다시 선택해 주세요.";
        return false;
    }

    public async Task InitializeAsync(Guid customerId, string customerName)
        => await InitializeAsync(
            customerId,
            customerName,
            _sessionStore.CaptureOwner());

    public async Task InitializeAsync(
        Guid customerId,
        string customerName,
        MobileSessionOwner contextOwner)
    {
        ArgumentNullException.ThrowIfNull(contextOwner);
        using (await _sessionStore
                   .AcquireOwnerCommitLeaseAsync(
                       contextOwner))
        {
            _ownerOperations.EnsureCurrentOwner(
                ResetForOwner);
            _contextOwner = contextOwner;
            _customerId = customerId;
            CustomerName =
                string.IsNullOrWhiteSpace(customerName)
                    ? "거래처 계약서"
                    : customerName;
        }

        await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_customerId == Guid.Empty)
            return;

        var operation = _ownerOperations.TryBegin(
            ResetForOwner,
            deferRefreshWhenBusy: true);
        IsBusy = _ownerOperations.IsBusy;
        if (operation is null)
            return;

        CacheOwnerSession? ownerSession = null;
        var runDeferredRefresh = false;
        try
        {
            if (!IsContextOwner(operation.Owner))
            {
                if (_ownerOperations.CanCommit(operation))
                {
                    StatusMessage =
                        "로그인 사용자가 변경되었습니다. 이전 화면으로 돌아가 거래처를 다시 선택해 주세요.";
                }
                return;
            }

            StatusMessage = "계약서를 서버에서 조회하고 있습니다.";
            ownerSession = _cacheStore.CaptureOwnerSession();
            ThrowIfCacheAndApiOwnersDiffer(
                ownerSession,
                operation.Owner);
            var result = await _api.GetCustomerContractsAsync(
                _customerId,
                operation.Owner);
            if (!_ownerOperations.CanCommit(operation))
                return;

            await _cacheStore.SaveContractsAsync(
                ownerSession,
                _customerId,
                result);
            if (!_ownerOperations.CanCommit(operation))
                return;

            var committedContracts =
                await _cacheStore.LoadContractsAsync(
                    ownerSession,
                    _customerId);
            if (!_ownerOperations.CanCommit(operation))
                return;

            ReplaceContracts(committedContracts);

            StatusMessage = committedContracts.Count == 0
                ? "등록된 계약서가 없습니다."
                : $"계약서 {committedContracts.Count:N0}건을 불러왔습니다.";
        }
        catch (Exception ex)
        {
            if (!_ownerOperations.CanCommit(operation))
                return;

            if (MobileRetryableNetworkFailure.IsRetryable(ex))
            {
                IReadOnlyList<CustomerContractDto> cachedContracts =
                    ownerSession is null
                    ? Array.Empty<CustomerContractDto>()
                    : await _cacheStore.LoadContractsAsync(
                        ownerSession,
                        _customerId);
                if (!_ownerOperations.CanCommit(operation))
                    return;

                ReplaceContracts(cachedContracts);

                StatusMessage = cachedContracts.Count == 0
                    ? $"계약서 조회 실패: {ex.Message}"
                    : $"서버 연결에 실패해 캐시 계약서 {cachedContracts.Count:N0}건을 표시합니다. ({ex.Message})";
                return;
            }

            ReplaceContracts(Array.Empty<CustomerContractDto>());
            StatusMessage = $"계약서 조회 실패: {ex.Message}";
        }
        finally
        {
            runDeferredRefresh = _ownerOperations.Complete(
                operation,
                ResetForOwner);
            IsBusy = _ownerOperations.IsBusy;
            if (runDeferredRefresh)
                await RefreshAsync();
        }
    }

    public async Task OpenContractAsync(CustomerContractDto? contract)
    {
        var operation = _ownerOperations.TryBegin(
            ResetForOwner,
            deferRefreshWhenBusy: false);
        IsBusy = _ownerOperations.IsBusy;
        if (operation is null)
            return;

        try
        {
            if (!IsContextOwner(operation.Owner))
            {
                if (_ownerOperations.CanCommit(operation))
                {
                    StatusMessage =
                        "로그인 사용자가 변경되었습니다. 이전 화면으로 돌아가 거래처를 다시 선택해 주세요.";
                }
                return;
            }

            if (contract is null)
            {
                StatusMessage =
                    "열 계약서를 선택하세요.";
                return;
            }

            var ownerSession = _cacheStore.CaptureOwnerSession();
            ThrowIfCacheAndApiOwnersDiffer(
                ownerSession,
                operation.Owner);
            var path = await _cacheStore.EnsureCachedPdfAsync(
                ownerSession,
                _customerId,
                contract);
            if (!_ownerOperations.CanCommit(operation))
                return;

            if (string.IsNullOrWhiteSpace(path))
            {
                var downloadedPath =
                    await _api.DownloadCustomerContractAsync(
                        contract,
                        operation.Owner);
                if (!_ownerOperations.CanCommit(operation))
                    return;

                path = await _cacheStore.CachePdfAsync(
                    ownerSession,
                    _customerId,
                    contract,
                    downloadedPath);
            }

            if (!_ownerOperations.CanCommit(operation))
                return;
            _cacheStore.ThrowIfOwnerSessionStale(
                ownerSession);
            await Launcher.Default.OpenAsync(new OpenFileRequest(contract.FileName, new ReadOnlyFile(path)));
            if (_ownerOperations.CanCommit(operation))
                StatusMessage = "계약서 PDF를 열었습니다.";
        }
        catch (Exception ex)
        {
            if (_ownerOperations.CanCommit(operation))
                StatusMessage = $"계약서 열기 실패: {ex.Message}";
        }
        finally
        {
            _ownerOperations.Complete(
                operation,
                ResetForOwner);
            IsBusy = _ownerOperations.IsBusy;
        }
    }

    private bool IsContextOwner(
        MobileSessionOwner owner)
        => _contextOwner is not null &&
           _contextOwner.IsAuthenticated ==
           owner.IsAuthenticated &&
           _contextOwner.HasSameLogicalOwner(owner) &&
           string.Equals(
               _contextOwner.SessionGeneration,
               owner.SessionGeneration,
               StringComparison.Ordinal);

    private static void ThrowIfCacheAndApiOwnersDiffer(
        CacheOwnerSession cacheOwner,
        MobileSessionOwner apiOwner)
    {
        if (!cacheOwner.HasSameOwnerAndSession(apiOwner))
        {
            throw new StaleMobileSessionOwnerException(
                "The contract cache owner and API owner were captured from different authenticated sessions.");
        }
    }

    private void ResetForOwner()
    {
        Contracts.Clear();
        CustomerName = "거래처 계약서";
        StatusMessage =
            "로그인 사용자가 변경되었습니다. 거래처를 다시 선택해 주세요.";
    }

    private void ReplaceContracts(IReadOnlyList<CustomerContractDto> contracts)
    {
        Contracts.Clear();
        foreach (var contract in contracts)
            Contracts.Add(contract);
    }
}
