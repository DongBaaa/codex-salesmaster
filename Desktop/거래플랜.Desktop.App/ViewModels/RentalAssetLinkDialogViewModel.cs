using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalAssetLinkDialogViewModel : ObservableObject
{
    private readonly RentalStateService _rental;
    private readonly SessionState _session;
    private readonly Guid? _currentBillingProfileId;
    private readonly Guid? _currentCustomerId;
    private readonly string _currentCustomerName;
    private readonly string _currentOfficeCode;
    private readonly string _defaultInstallLocation;
    private readonly List<RentalBillingAssetOption> _assetPool = new();

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _includeRelinkTargets = true;
    [ObservableProperty] private bool _includeOtherOfficeAssets = true;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "설치현황 장비를 불러오는 중입니다.";
    [ObservableProperty] private RentalBillingAssetOption? _selectedAsset;

    public ObservableCollection<RentalBillingAssetOption> Assets { get; } = new();

    public string CurrentCustomerName => string.IsNullOrWhiteSpace(_currentCustomerName) ? "(거래처 미지정)" : _currentCustomerName;
    public string CurrentOfficeName => OfficeCodeCatalog.GetOfficeDisplayName(_currentOfficeCode);
    public int SelectedCount => _assetPool.Count(asset => asset.IsSelected);
    public bool CanConfirm => SelectedCount > 0;

    public RentalAssetLinkDialogViewModel(
        RentalStateService rental,
        SessionState session,
        Guid? currentBillingProfileId,
        Guid? currentCustomerId,
        string? currentCustomerName,
        string? currentOfficeCode,
        string? defaultInstallLocation)
    {
        _rental = rental;
        _session = session;
        _currentBillingProfileId = currentBillingProfileId;
        _currentCustomerId = currentCustomerId;
        _currentCustomerName = (currentCustomerName ?? string.Empty).Trim();
        _currentOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(currentOfficeCode, session.OfficeCode);
        _defaultInstallLocation = (defaultInstallLocation ?? string.Empty).Trim();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnIncludeRelinkTargetsChanged(bool value) => ApplyFilter();

    partial void OnIncludeOtherOfficeAssetsChanged(bool value)
    {
        UiTaskHelper.Forget(
            LoadAsync(),
            "RENTAL",
            "렌탈 자산 연결 후보 다시 불러오기");
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        IsBusy = true;
        try
        {
            var candidates = await _rental.GetAssetLinkCandidatesAsync(
                _currentBillingProfileId,
                _currentCustomerId,
                _currentCustomerName,
                _currentOfficeCode,
                _session,
                IncludeOtherOfficeAssets,
                ct);

            _assetPool.Clear();
            foreach (var candidate in candidates)
            {
                var source = candidate.Source;
                var option = new RentalBillingAssetOption
                {
                    AssetId = source.Id,
                    CustomerId = _currentCustomerId,
                    BillingProfileId = source.BillingProfileId,
                    ManagementNumber = source.ManagementNumber,
                    ItemName = source.ItemName,
                    ItemCategoryName = source.ItemCategoryName,
                    Manufacturer = source.Manufacturer,
                    MachineNumber = source.MachineNumber,
                    CurrentCustomerName = candidate.CustomerDisplayName,
                    TargetCustomerName = CurrentCustomerName,
                    InstallLocation = string.IsNullOrWhiteSpace(source.InstallLocation)
                        ? string.IsNullOrWhiteSpace(source.InstallSiteName)
                            ? _defaultInstallLocation
                            : source.InstallSiteName
                        : source.InstallLocation,
                    AssetStatus = source.AssetStatus,
                    BillingEligibilityStatus = string.IsNullOrWhiteSpace(source.BillingEligibilityStatus)
                        ? "미확인"
                        : source.BillingEligibilityStatus,
                    CurrentBillingProfileDisplay = string.IsNullOrWhiteSpace(candidate.CurrentBillingProfileDisplay)
                        ? "미연결"
                        : candidate.CurrentBillingProfileDisplay,
                    ResponsibleOfficeName = candidate.ResponsibleOfficeName,
                    ManagementCompanyName = candidate.ManagementCompanyName,
                    AssetScopeDisplay = candidate.AssetScopeDisplay,
                    IsOutsideCurrentOffice = candidate.IsOutsideCurrentOffice,
                    Notes = source.Notes ?? string.Empty,
                    MonthlyFee = source.MonthlyFee,
                    ContractStartDate = ToDateTime(source.ContractStartDate ?? source.InstallDate ?? source.ContractDate),
                    PurchaseDate = ToDateTime(source.PurchaseDate),
                    InstallDate = ToDateTime(source.InstallDate),
                    IsSelected = _currentBillingProfileId.HasValue &&
                                 _currentBillingProfileId.Value != Guid.Empty &&
                                 source.BillingProfileId == _currentBillingProfileId.Value,
                    IsLinkedToCurrentProfile = _currentBillingProfileId.HasValue &&
                                               _currentBillingProfileId.Value != Guid.Empty &&
                                               source.BillingProfileId == _currentBillingProfileId.Value,
                    IsLinkedToAnotherProfile = source.BillingProfileId.HasValue &&
                                               source.BillingProfileId.Value != Guid.Empty &&
                                               (!_currentBillingProfileId.HasValue || source.BillingProfileId.Value != _currentBillingProfileId.Value)
                };
                option.PropertyChanged += HandleAssetOptionPropertyChanged;
                _assetPool.Add(option);
            }

            ApplyFilter();
            SelectedAsset = Assets.FirstOrDefault(asset => asset.IsSelected) ?? Assets.FirstOrDefault();
            StatusMessage = _assetPool.Count == 0
                ? "연결 가능한 설치현황 장비가 없습니다."
                : $"설치현황 자산 {_assetPool.Count:N0}대를 불러왔습니다. {BuildScopeStatusSuffix()} 시리얼번호/관리번호/거래처명으로 바로 검색할 수 있습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task ReloadAsync()
        => LoadAsync();

    public IReadOnlyList<RentalBillingAssetOption> GetSelectedAssets()
        => _assetPool
            .Where(asset => asset.IsSelected)
            .Select(asset => CloneAsset(asset))
            .ToList();

    public int GetRelinkSelectionCount()
        => _assetPool.Count(asset => asset.IsSelected && asset.IsLinkedToAnotherProfile);

    private void ApplyFilter()
    {
        var keyword = (SearchText ?? string.Empty).Trim();

        Assets.Clear();
        foreach (var asset in _assetPool.Where(asset => MatchesFilter(asset, keyword)))
            Assets.Add(asset);

        if (SelectedAsset is null || !Assets.Contains(SelectedAsset))
            SelectedAsset = Assets.FirstOrDefault();

        StatusMessage = _assetPool.Count == 0
            ? "연결 가능한 설치현황 장비가 없습니다."
            : SelectedCount > 0
                ? $"선택 장비 {SelectedCount:N0}대를 현재 거래처에 연결할 예정입니다. {BuildScopeStatusSuffix()}"
                : $"표시 장비 {Assets.Count:N0}대 / 전체 {_assetPool.Count:N0}대 ({BuildScopeStatusSuffix()})";
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(CanConfirm));
    }

    private bool MatchesFilter(RentalBillingAssetOption asset, string keyword)
    {
        if (!IncludeRelinkTargets && asset.IsLinkedToAnotherProfile && !asset.IsSelected)
            return false;

        if (string.IsNullOrWhiteSpace(keyword))
            return true;

        return (asset.ManagementNumber ?? string.Empty).Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
               (asset.ItemName ?? string.Empty).Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
               (asset.MachineNumber ?? string.Empty).Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
               (asset.CurrentCustomerName ?? string.Empty).Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
               (asset.InstallLocation ?? string.Empty).Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
               (asset.CurrentBillingProfileDisplay ?? string.Empty).Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
               (asset.ResponsibleOfficeName ?? string.Empty).Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
               (asset.ManagementCompanyName ?? string.Empty).Contains(keyword, StringComparison.CurrentCultureIgnoreCase) ||
               (asset.AssetScopeDisplay ?? string.Empty).Contains(keyword, StringComparison.CurrentCultureIgnoreCase);
    }

    private void HandleAssetOptionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not RentalBillingAssetOption asset)
            return;

        if (string.Equals(e.PropertyName, nameof(RentalBillingAssetOption.IsSelected), StringComparison.Ordinal))
        {
            if (asset.IsSelected && string.IsNullOrWhiteSpace(asset.InstallLocation) && !string.IsNullOrWhiteSpace(_defaultInstallLocation))
                asset.InstallLocation = _defaultInstallLocation;

            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(CanConfirm));
            StatusMessage = SelectedCount > 0
                ? $"선택 장비 {SelectedCount:N0}대를 현재 거래처에 연결할 예정입니다. {BuildScopeStatusSuffix()}"
                : $"표시 장비 {Assets.Count:N0}대 / 전체 {_assetPool.Count:N0}대 ({BuildScopeStatusSuffix()})";
            return;
        }

        if (SelectedCount > 0)
            StatusMessage = $"선택 장비 {SelectedCount:N0}대의 상세정보를 저장 대기 중입니다.";
    }

    private static RentalBillingAssetOption CloneAsset(RentalBillingAssetOption asset)
        => new()
        {
            AssetId = asset.AssetId,
            CustomerId = asset.CustomerId,
            BillingProfileId = asset.BillingProfileId,
            ManagementNumber = asset.ManagementNumber,
            ItemName = asset.ItemName,
            ItemCategoryName = asset.ItemCategoryName,
            Manufacturer = asset.Manufacturer,
            MachineNumber = asset.MachineNumber,
            CurrentCustomerName = asset.CurrentCustomerName,
            TargetCustomerName = asset.TargetCustomerName,
            InstallLocation = asset.InstallLocation,
            AssetStatus = asset.AssetStatus,
            BillingEligibilityStatus = asset.BillingEligibilityStatus,
            CurrentBillingProfileDisplay = asset.CurrentBillingProfileDisplay,
            ResponsibleOfficeName = asset.ResponsibleOfficeName,
            ManagementCompanyName = asset.ManagementCompanyName,
            AssetScopeDisplay = asset.AssetScopeDisplay,
            IsOutsideCurrentOffice = asset.IsOutsideCurrentOffice,
            Notes = asset.Notes,
            MonthlyFee = asset.MonthlyFee,
            ContractStartDate = asset.ContractStartDate,
            PurchaseDate = asset.PurchaseDate,
            InstallDate = asset.InstallDate,
            IsLinkedToCurrentProfile = asset.IsLinkedToCurrentProfile,
            IsLinkedToAnotherProfile = asset.IsLinkedToAnotherProfile,
            IsSelected = asset.IsSelected
        };

    private string BuildScopeStatusSuffix()
    {
        if (!IncludeOtherOfficeAssets)
            return "현재 담당지점 자산만 표시 중입니다.";

        var outsideCount = _assetPool.Count(asset => asset.IsOutsideCurrentOffice);
        return outsideCount > 0
            ? $"다른 담당지점 자산 {outsideCount:N0}대 포함 중입니다."
            : "다른 담당지점 자산까지 확인 중입니다.";
    }

    private static DateTime? ToDateTime(DateOnly? value)
        => value?.ToDateTime(TimeOnly.MinValue);
}
