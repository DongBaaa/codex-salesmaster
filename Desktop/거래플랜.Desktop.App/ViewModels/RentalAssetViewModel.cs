using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalAssetViewModel : ObservableObject
{
    private const string AllOption = "전체";

    private readonly RentalStateService _rental;
    private readonly LocalStateService _local;
    private readonly RentalDocumentService _documents;
    private readonly IPrintService _printService;
    private readonly SessionState _session;
    private readonly UiDebouncer _searchDebouncer = new();
    private readonly SemaphoreSlim _autoSaveGate = new(1, 1);
    private bool _suppressFilterReload;
    private bool _suppressSelectionAutoSave;
    private bool _pendingFilterReload;
    private string _baselineStateSignature = string.Empty;
    private long _editRevision;

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isOfficeFilterPopupOpen;
    [ObservableProperty] private bool _isItemCategoryFilterPopupOpen;
    [ObservableProperty] private bool _isStatusFilterPopupOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "렌탈 자산을 불러오는 중입니다.";
    [ObservableProperty] private RentalAssetViewRow? _selectedRow;
    [ObservableProperty] private RentalAssetAssignmentHistoryViewItem? _selectedAssignmentHistory;

    [ObservableProperty] private Guid _editId = Guid.NewGuid();
    [ObservableProperty] private Guid? _editCustomerId;
    [ObservableProperty] private Guid? _editItemId;
    [ObservableProperty] private string _editManagementId = string.Empty;
    [ObservableProperty] private string _editManagementNumber = string.Empty;
    [ObservableProperty] private string _editOfficeCode = string.Empty;
    [ObservableProperty] private string _editCurrentLocation = string.Empty;
    [ObservableProperty] private string _editItemCategoryName = string.Empty;
    [ObservableProperty] private string _editManufacturer = string.Empty;
    [ObservableProperty] private string _editItemName = string.Empty;
    [ObservableProperty] private string _editMachineNumber = string.Empty;
    [ObservableProperty] private string _editPurchaseVendor = string.Empty;
    [ObservableProperty] private decimal _editPurchasePrice;
    [ObservableProperty] private decimal _editSalePrice;
    [ObservableProperty] private string _editCustomerName = string.Empty;
    [ObservableProperty] private string _editCurrentCustomerName = string.Empty;
    [ObservableProperty] private string _editInstallLocation = string.Empty;
    [ObservableProperty] private string _editLastCustomerName = string.Empty;
    [ObservableProperty] private string _editLastInstallLocation = string.Empty;
    [ObservableProperty] private string _editLastBillingProfileDisplay = string.Empty;
    [ObservableProperty] private string _editLastAssignmentClearedAtText = string.Empty;
    [ObservableProperty] private string _editDepositText = string.Empty;
    [ObservableProperty] private decimal _editMonthlyFee;
    [ObservableProperty] private int _editContractMonths;
    [ObservableProperty] private string _editFreeSupplyItems = string.Empty;
    [ObservableProperty] private string _editPaidSupplyItems = string.Empty;
    [ObservableProperty] private string _editAssetStatus = "임대진행중";
    [ObservableProperty] private string _editBillingEligibilityStatus = "미확인";
    [ObservableProperty] private string _editBillingExclusionReason = string.Empty;
    [ObservableProperty] private string _editNotes = string.Empty;
    [ObservableProperty] private DateTime? _editPurchaseDate;
    [ObservableProperty] private DateTime? _editDisposalDate;
    [ObservableProperty] private DateTime? _editContractDate;
    [ObservableProperty] private DateTime? _editInstallDate;
    [ObservableProperty] private DateTime? _editContractStartDate;
    [ObservableProperty] private DateTime? _editRentalEndDate;

    public ObservableCollection<SelectableFilterOption> OfficeFilterOptions { get; } = new();
    public ObservableCollection<DisplayOption> EditOfficeOptions { get; } = new();
    public ObservableCollection<SelectableFilterOption> ItemCategoryFilterOptions { get; } = new();
    public ObservableCollection<SelectableFilterOption> StatusFilterOptions { get; } = new();
    public ObservableCollection<string> AssetStatusOptions { get; } = new();
    public ObservableCollection<string> EditableAssetStatusOptions { get; } = new();
    public ObservableCollection<string> BillingEligibilityStatusOptions { get; } = new();
    public ObservableCollection<LocalItemCategoryOption> ItemCategoryOptions { get; } = new();
    public ObservableCollection<RentalAssetViewRow> Rows { get; } = new();
    public ObservableCollection<RentalAssetAssignmentHistoryViewItem> AssignmentHistories { get; } = new();

    public bool CanViewAll => _rental.CanViewAllAssetScope(_session);
    public bool CanManageAll => _rental.CanManageAllAssetScope(_session);
    public bool CanEditOfficeSelection => CanManageAll;
    public bool CanSave => SelectedRow is null || CanEditCurrentSelection;
    public bool CanDeleteSelected => SelectedRow is not null && CanEditCurrentSelection;
    public bool IsNewAsset => SelectedRow is null;
    public bool IsNonOperatingAssetStatus => RentalAssetStatusRules.IsNonOperating(EditAssetStatus);
    public bool CanEditAssignmentFields => !IsNonOperatingAssetStatus;
    public bool HasLastAssignmentHistory =>
        !string.IsNullOrWhiteSpace(EditLastCustomerName) ||
        !string.IsNullOrWhiteSpace(EditLastInstallLocation) ||
        !string.IsNullOrWhiteSpace(EditLastBillingProfileDisplay) ||
        !string.IsNullOrWhiteSpace(EditLastAssignmentClearedAtText);
    public bool ShowLastAssignmentHistory => IsNonOperatingAssetStatus && HasLastAssignmentHistory;
    public bool HasPendingChanges => !string.Equals(_baselineStateSignature, BuildEditStateSignature(CaptureEditSnapshot()), StringComparison.Ordinal);
    public bool HasMeaningfulDraftContentForClose => HasMeaningfulDraftContent(CaptureEditSnapshot());
    public string SelectedOfficeFilterSummary => BuildFilterSummary(OfficeFilterOptions, "담당지점");
    public string SelectedItemCategoryFilterSummary => BuildFilterSummary(ItemCategoryFilterOptions, "품목분류");
    public string SelectedStatusFilterSummary => BuildFilterSummary(StatusFilterOptions, "상태");
    public string AssignmentFieldsNotice => IsNonOperatingAssetStatus
        ? $"'{EditAssetStatus}' 상태에서는 거래처/설치/청구 연결이 필요하지 않습니다. 저장 시 관련 정보가 정리됩니다."
        : string.Empty;
    public LocalStateService LocalStateService => _local;
    public SessionState SessionState => _session;

    private bool CanEditCurrentSelection => SelectedRow is null || _rental.CanEditAssetScope(
        string.IsNullOrWhiteSpace(SelectedRow.Source.ResponsibleOfficeCode)
            ? SelectedRow.Source.ManagementCompanyCode
            : SelectedRow.Source.ResponsibleOfficeCode,
        _session);

    public RentalAssetViewModel(
        RentalStateService rental,
        LocalStateService local,
        RentalDocumentService documents,
        IPrintService printService,
        SessionState session)
    {
        _rental = rental;
        _local = local;
        _documents = documents;
        _printService = printService;
        _session = session;
        _local.InventoryStateChanged += HandleInventoryStateChanged;

        AssetStatusOptions.Add(AllOption);
        AssetStatusOptions.Add("임대진행중");
        AssetStatusOptions.Add("대기");
        AssetStatusOptions.Add("창고");
        AssetStatusOptions.Add("회수");
        AssetStatusOptions.Add("점검중");
        AssetStatusOptions.Add("판매");
        AssetStatusOptions.Add("폐기");
        AssetStatusOptions.Add("미배정");
        AssetStatusOptions.Add("설치처 불명");

        foreach (var status in AssetStatusOptions.Where(status => status != AllOption))
        {
            EditableAssetStatusOptions.Add(status);
            var option = new SelectableFilterOption(status, status);
            option.PropertyChanged += HandleFilterOptionPropertyChanged;
            StatusFilterOptions.Add(option);
        }

        BillingEligibilityStatusOptions.Add("청구대상");
        BillingEligibilityStatusOptions.Add("청구제외");
        BillingEligibilityStatusOptions.Add("미확인");
        ResetEditBaseline();
    }

    public async Task LoadAsync()
    {
        await ReloadFiltersAsync();
        await ReloadItemCategoryOptionsAsync();
        await ReloadAsync();
        ResetForNewAsset();
    }

    public async Task LoadAndSelectAssetAsync(Guid assetId)
    {
        await LoadAsync();
        SelectRowWithoutAutoSave(assetId);
        StatusMessage = SelectedRow is null
            ? "점검 항목의 렌탈 자산을 목록에서 찾지 못했습니다. 필터, 권한, 삭제 상태를 확인하세요."
            : "운영 점검 항목의 렌탈 자산을 선택했습니다. 월요금, 청구대상 여부, 청구 프로필 연결을 확인한 뒤 저장하세요.";
    }

    public async Task ApplyInitialCustomerFilterAsync(LocalCustomer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);

        var normalizedCustomerName = (customer.NameOriginal ?? string.Empty).Trim();
        var normalizedOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(customer.ResponsibleOfficeCode, _session.OfficeCode);

        _suppressFilterReload = true;
        try
        {
            SearchText = normalizedCustomerName;
            if (!string.IsNullOrWhiteSpace(normalizedOfficeCode))
                SetSelectedFilterValues(OfficeFilterOptions, [normalizedOfficeCode]);
        }
        finally
        {
            _suppressFilterReload = false;
        }

        await ReloadAsync();
    }

    partial void OnSearchTextChanged(string value) => RequestFilterReload();

    partial void OnSelectedRowChanging(RentalAssetViewRow? oldValue, RentalAssetViewRow? newValue)
    {
        if (_suppressSelectionAutoSave || ReferenceEquals(oldValue, newValue))
            return;

        if (!TryCaptureAutoSaveSnapshot(out var snapshot))
            return;

        UiTaskHelper.Forget(
            HandleSelectionAutoSaveAsync(snapshot, oldValue, newValue),
            "RENTAL",
            "렌탈 자산 선택 변경 자동저장",
            ex => StatusMessage = $"렌탈 자산 자동저장 중 오류가 발생했습니다. {ex.Message}");
    }

    partial void OnEditCustomerNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(EditCurrentCustomerName))
            EditCurrentCustomerName = value;
    }

    partial void OnEditAssetStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsNonOperatingAssetStatus));
        OnPropertyChanged(nameof(CanEditAssignmentFields));
        OnPropertyChanged(nameof(ShowLastAssignmentHistory));
        OnPropertyChanged(nameof(AssignmentFieldsNotice));
        ApplyAssetStatusUiRules();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        if (IsBusy)
        {
            _pendingFilterReload = true;
            return;
        }

        do
        {
            _pendingFilterReload = false;
            IsBusy = true;
            try
            {
                var selectedRowId = SelectedRow?.Source.Id;
                var rows = await _rental.GetAssetRowsAsync(new RentalAssetFilter
                {
                    SearchText = SearchText,
                    ItemCategoryNames = GetSelectedFilterValues(ItemCategoryFilterOptions),
                    OfficeCodes = GetSelectedFilterValues(OfficeFilterOptions),
                    AssetStatuses = GetSelectedFilterValues(StatusFilterOptions)
                }, _session);

                Rows.Clear();
                foreach (var row in rows)
                    Rows.Add(row);

                if (selectedRowId.HasValue)
                {
                    SelectedRow = Rows.FirstOrDefault(row => row.Source.Id == selectedRowId.Value);
                    if (SelectedRow is null)
                        ResetForNewAsset();
                }

                StatusMessage = rows.Count == 0
                    ? "조건에 맞는 렌탈 자산이 없습니다."
                    : $"렌탈 자산 {rows.Count:N0}건을 조회했습니다.";
            }
            finally
            {
                IsBusy = false;
            }
        }
        while (_pendingFilterReload);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var snapshot = CaptureEditSnapshot();
        await SaveSnapshotAsync(
            snapshot,
            preserveSelectionRowId: snapshot.EditId,
            refreshAfterSave: true,
            successMessage: "렌탈 자산을 저장했습니다.",
            permissionDeniedMessage: "현재 선택한 렌탈 자산을 저장할 권한이 없습니다.",
            showConflictDialog: true);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedRow is null)
        {
            StatusMessage = "삭제할 렌탈 자산을 선택하세요.";
            return;
        }

        var targetAssetId = SelectedRow.Source.Id;
        var result = await _rental.DeleteAssetAsync(targetAssetId, _session, SelectedRow.Source.Revision);
        if (!result.Success)
        {
            StatusMessage = result.Message;
            if (result.ConcurrencyConflict)
            {
                await ReloadAsync();
                SelectRowWithoutAutoSave(targetAssetId);
                MessageBox.Show(
                    result.Message,
                    "동시 수정 충돌",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        await ReloadAsync();
        ResetForNewAsset();
    }

    [RelayCommand]
    private async Task DeleteCheckedAsync()
    {
        var targets = Rows
            .Where(row => row.IsSelected)
            .ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "삭제할 렌탈 자산을 먼저 선택하세요.";
            return;
        }

        var confirmation = MessageBox.Show(
            $"선택한 {targets.Count:N0}건을 삭제하시겠습니까?",
            "렌탈 자산 선택삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
            return;

        var successCount = 0;
        var conflictCount = 0;
        var failureMessages = new List<string>();
        foreach (var row in targets)
        {
            var result = await _rental.DeleteAssetAsync(row.Source.Id, _session, row.Source.Revision);
            if (result.Success)
            {
                successCount++;
                continue;
            }

            if (result.ConcurrencyConflict)
                conflictCount++;
            failureMessages.Add($"{row.Source.CustomerName}: {result.Message}");
        }

        await ReloadAsync();
        ResetForNewAsset();

        StatusMessage = failureMessages.Count == 0
            ? $"선택한 렌탈 자산 {successCount:N0}건을 삭제했습니다."
            : $"삭제 성공 {successCount:N0}건 / 실패 {failureMessages.Count:N0}건 - {string.Join(" | ", failureMessages.Take(3))}";

        if (conflictCount > 0)
        {
            MessageBox.Show(
                $"{conflictCount:N0}건은 다른 PC에서 먼저 수정되어 삭제하지 못했습니다. 최신 목록을 다시 불러왔습니다.",
                "동시 수정 충돌",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task NewAsset()
    {
        if (!await TryAutoSaveCurrentEditAsync(refreshAfterSave: true))
            return;

        ResetForNewAsset("신규 렌탈 자산 정보를 입력하세요.");
    }

    [RelayCommand]
    private async Task OpenReturnReportAsync()
    {
        if (!TryBuildDocumentAsset(out var asset))
            return;

        var company = await _local.GetCompanyProfileAsync(_session);
        var inputWindow = new RentalReturnReportInputWindow(BuildDefaultReturnReason(asset))
        {
            Owner = GetActiveWindow()
        };
        if (inputWindow.ShowDialog() != true)
        {
            StatusMessage = "회수장비내역서 작성을 취소했습니다.";
            return;
        }

        var managementCompanyNames = await BuildManagementCompanyNameLookupAsync();
        var document = _documents.BuildReturnReportDocument(asset, company, inputWindow.ReportFields, managementCompanyNames);
        OpenPreview(document, $"회수장비내역서_{BuildSafeDocumentSuffix(asset)}");
        StatusMessage = "회수장비내역서를 열었습니다.";
    }

    [RelayCommand]
    private async Task OpenEquipmentDetailAsync()
    {
        if (!TryBuildDocumentAsset(out var asset))
            return;

        var company = await _local.GetCompanyProfileAsync(_session);
        var customer = await ResolveDocumentCustomerAsync(asset);
        var relatedAssets = (await _rental.GetAssetsForEquipmentDetailAsync(asset, _session)).ToList();
        MergeCurrentDocumentAsset(asset, relatedAssets);

        var managementCompanyNames = await BuildManagementCompanyNameLookupAsync();
        var document = _documents.BuildEquipmentDetailDocument(relatedAssets, customer, company, managementCompanyNames);
        OpenPreview(document, $"렌탈장비내역서_{BuildSafeDocumentSuffix(asset)}");
        StatusMessage = $"렌탈장비내역서({relatedAssets.Count:N0}대)를 열었습니다.";
    }

    [RelayCommand]
    private async Task OpenContractWriterAsync()
    {
        if (!TryBuildDocumentAsset(out var asset))
            return;

        var company = await _local.GetCompanyProfileAsync(_session);
        if (company is null)
        {
            StatusMessage = "현재 로그인 사용자의 회사설정을 먼저 지정하세요.";
            return;
        }

        var customer = await ResolveDocumentCustomerAsync(asset);
        var preferredCustomerContractDate = customer?.Id is Guid customerId && customerId != Guid.Empty
            ? (await _local.GetRepresentativeCustomerContractAsync(customerId, _session))?.SignedDate
            : null;
        var officeOptions = EditOfficeOptions
            .Select(option => new DisplayOption
            {
                Value = option.Value,
                DisplayName = option.DisplayName
            })
            .ToList();
        var companyProfiles = await _local.GetCompanyProfilesAsync();

        var contractModel = _documents.CreateContractDocumentModel(asset, customer, company, preferredCustomerContractDate);
        var editorViewModel = new RentalContractEditorViewModel(
            contractModel,
            _documents,
            officeOptions,
            companyProfiles,
            string.IsNullOrWhiteSpace(asset.ResponsibleOfficeCode) ? company.OfficeCode : asset.ResponsibleOfficeCode,
            CanManageAll);
        var editorWindow = new RentalContractEditorWindow(editorViewModel)
        {
            Owner = GetActiveWindow()
        };
        editorWindow.ShowDialog();
        StatusMessage = "렌탈계약서 작성창을 열었습니다.";
    }

    [RelayCommand]
    private async Task AddAssignmentHistoryAsync()
    {
        if (SelectedRow is null)
        {
            StatusMessage = "임대이력을 추가할 렌탈 자산을 먼저 선택하세요.";
            return;
        }

        var request = await _rental.CreateAssetAssignmentHistoryEditRequestAsync(SelectedRow.Source.Id);
        if (request is null)
        {
            StatusMessage = "임대이력 추가 정보를 만들 수 없습니다.";
            return;
        }

        if (!ShowAssignmentHistoryDialog(request, "임대이력 추가"))
            return;

        await SaveAssignmentHistoryRequestAsync(request);
    }

    [RelayCommand]
    private async Task EditAssignmentHistoryAsync()
    {
        if (SelectedRow is null || SelectedAssignmentHistory is null)
        {
            StatusMessage = "수정할 임대이력을 먼저 선택하세요.";
            return;
        }

        var request = await _rental.CreateAssetAssignmentHistoryEditRequestAsync(
            SelectedRow.Source.Id,
            SelectedAssignmentHistory.HistoryId);
        if (request is null)
        {
            StatusMessage = "수정할 임대이력을 찾을 수 없습니다.";
            return;
        }

        if (!ShowAssignmentHistoryDialog(request, "임대이력 수정"))
            return;

        await SaveAssignmentHistoryRequestAsync(request);
    }

    [RelayCommand]
    private async Task DeleteAssignmentHistoryAsync()
    {
        if (SelectedAssignmentHistory is null)
        {
            StatusMessage = "삭제할 임대이력을 먼저 선택하세요.";
            return;
        }

        var confirmation = MessageBox.Show(
            "선택한 임대이력을 삭제하시겠습니까?\n자산 자체는 삭제되지 않고, 임대이력만 삭제됩니다.",
            "임대이력 삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
            return;

        var assetId = SelectedAssignmentHistory.AssetId;
        IsBusy = true;
        try
        {
            var result = await _rental.DeleteAssetAssignmentHistoryAsync(SelectedAssignmentHistory.HistoryId, _session);
            StatusMessage = result.Message;
            if (!result.Success)
            {
                MessageBox.Show(result.Message, "임대이력 삭제", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await LoadAssignmentHistoriesAsync(assetId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool ShowAssignmentHistoryDialog(RentalAssetAssignmentHistoryEditRequest request, string title)
    {
        var dialog = new RentalAssignmentHistoryEditWindow(request)
        {
            Owner = GetActiveWindow(),
            Title = title
        };
        return dialog.ShowDialog() == true;
    }

    private async Task SaveAssignmentHistoryRequestAsync(RentalAssetAssignmentHistoryEditRequest request)
    {
        IsBusy = true;
        try
        {
            var result = await _rental.SaveAssetAssignmentHistoryAsync(request, _session);
            StatusMessage = result.Message;
            if (!result.Success)
            {
                MessageBox.Show(result.Message, "임대이력 저장", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await LoadAssignmentHistoriesAsync(request.AssetId);
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedRowChanged(RentalAssetViewRow? value)
    {
        if (value is null)
        {
            SelectedAssignmentHistory = null;
            AssignmentHistories.Clear();
            EditLastCustomerName = string.Empty;
            EditLastInstallLocation = string.Empty;
            EditLastBillingProfileDisplay = string.Empty;
            EditLastAssignmentClearedAtText = string.Empty;
            OnPropertyChanged(nameof(HasLastAssignmentHistory));
            OnPropertyChanged(nameof(ShowLastAssignmentHistory));
            OnPropertyChanged(nameof(IsNewAsset));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanDeleteSelected));
            return;
        }

        var source = value.Source;
        _editRevision = source.Revision;
        EditId = source.Id;
        EditCustomerId = source.CustomerId;
        EditItemId = source.ItemId;
        EditManagementId = source.ManagementId;
        EditManagementNumber = source.ManagementNumber;
        EditOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            string.IsNullOrWhiteSpace(source.ResponsibleOfficeCode)
                ? source.ManagementCompanyCode
                : source.ResponsibleOfficeCode,
            _session.OfficeCode);
        EnsureEditOfficeOption(EditOfficeCode);
        EditCurrentLocation = source.CurrentLocation;
        EditItemCategoryName = source.ItemCategoryName;
        EditManufacturer = source.Manufacturer;
        EditItemName = source.ItemName;
        EditMachineNumber = source.MachineNumber;
        EditPurchaseVendor = source.PurchaseVendor;
        EditPurchasePrice = source.PurchasePrice;
        EditSalePrice = source.SalePrice;
        EditCustomerName = source.CustomerName;
        EditCurrentCustomerName = string.IsNullOrWhiteSpace(source.CurrentCustomerName) ? source.CustomerName : source.CurrentCustomerName;
        EditInstallLocation = string.IsNullOrWhiteSpace(source.InstallLocation) ? source.InstallSiteName : source.InstallLocation;
        EditLastCustomerName = source.LastCustomerName;
        EditLastInstallLocation = source.LastInstallLocation;
        EditLastBillingProfileDisplay = source.LastBillingProfileDisplay;
        EditLastAssignmentClearedAtText = source.LastAssignmentClearedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;
        EditDepositText = source.DepositText;
        EditMonthlyFee = source.MonthlyFee;
        EditContractMonths = source.ContractMonths;
        EditFreeSupplyItems = source.FreeSupplyItems;
        EditPaidSupplyItems = source.PaidSupplyItems;
        EditAssetStatus = source.AssetStatus;
        EditBillingEligibilityStatus = string.IsNullOrWhiteSpace(source.BillingEligibilityStatus) ? "미확인" : source.BillingEligibilityStatus;
        EditBillingExclusionReason = source.BillingExclusionReason;
        EditNotes = source.Notes;
        EditPurchaseDate = ToDateTime(source.PurchaseDate);
        EditDisposalDate = ToDateTime(source.DisposalDate);
        EditContractDate = ToDateTime(source.ContractDate);
        EditInstallDate = ToDateTime(source.InstallDate);
        EditContractStartDate = ToDateTime(source.ContractStartDate);
        EditRentalEndDate = ToDateTime(source.RentalEndDate);
        ApplyAssetStatusUiRules();
        OnPropertyChanged(nameof(HasLastAssignmentHistory));
        OnPropertyChanged(nameof(ShowLastAssignmentHistory));
        OnPropertyChanged(nameof(IsNewAsset));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanDeleteSelected));
        ResetEditBaseline();
        UiTaskHelper.Forget(
            LoadAssignmentHistoriesAsync(source.Id),
            "RENTAL",
            "렌탈 자산 임대 이력 조회",
            ex => StatusMessage = $"임대 이력을 불러오지 못했습니다. {ex.Message}");
    }

    private async Task LoadAssignmentHistoriesAsync(Guid assetId)
    {
        if (assetId == Guid.Empty)
        {
            SelectedAssignmentHistory = null;
            AssignmentHistories.Clear();
            return;
        }

        var histories = await _rental.GetAssetAssignmentHistoriesAsync(assetId);
        if (SelectedRow?.Source.Id != assetId)
            return;

        SelectedAssignmentHistory = null;
        AssignmentHistories.Clear();
        foreach (var history in histories)
            AssignmentHistories.Add(history);
    }

    private void ApplyAssetStatusUiRules()
    {
        if (RentalAssetStatusRules.IsNonOperating(EditAssetStatus))
        {
            if (!string.Equals(EditBillingEligibilityStatus, "청구제외", StringComparison.OrdinalIgnoreCase))
                EditBillingEligibilityStatus = "청구제외";

            if (string.IsNullOrWhiteSpace(EditBillingExclusionReason))
                EditBillingExclusionReason = RentalAssetStatusRules.BuildAutoExclusionReason(EditAssetStatus);
            return;
        }

        if (string.Equals(EditBillingEligibilityStatus, "청구제외", StringComparison.OrdinalIgnoreCase) &&
            RentalAssetStatusRules.IsAutoGeneratedExclusionReason(EditBillingExclusionReason))
        {
            EditBillingEligibilityStatus = "미확인";
            EditBillingExclusionReason = string.Empty;
        }
    }

    private bool TryBuildDocumentAsset(out LocalRentalAsset asset)
    {
        if (string.IsNullOrWhiteSpace(EditCustomerName))
        {
            StatusMessage = "거래처명을 입력하거나 렌탈 자산을 선택하세요.";
            asset = new LocalRentalAsset();
            return false;
        }

        if (string.IsNullOrWhiteSpace(EditItemName))
        {
            StatusMessage = "품명을 입력하세요.";
            asset = new LocalRentalAsset();
            return false;
        }

        var documentOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(EditOfficeCode, _session.OfficeCode);
        var documentManagementCompanyCode = ResolveManagementCompanyCodeForCurrentAsset(documentOfficeCode);

        asset = new LocalRentalAsset
        {
            Id = EditId,
            CustomerId = EditCustomerId ?? SelectedRow?.Source.CustomerId,
            ItemId = EditItemId ?? SelectedRow?.Source.ItemId,
            BillingProfileId = SelectedRow?.Source.BillingProfileId,
            OfficeCode = documentOfficeCode,
            ManagementId = EditManagementId,
            ManagementNumber = EditManagementNumber,
            ManagementCompanyCode = documentManagementCompanyCode,
            CurrentLocation = EditCurrentLocation,
            ItemCategoryName = EditItemCategoryName,
            Manufacturer = EditManufacturer,
            ItemName = EditItemName,
            MachineNumber = EditMachineNumber,
            PurchaseVendor = EditPurchaseVendor,
            PurchaseDate = ToDateOnly(EditPurchaseDate),
            DisposalDate = ToDateOnly(EditDisposalDate),
            PurchasePrice = EditPurchasePrice,
            SalePrice = EditSalePrice,
            CustomerName = EditCustomerName,
            InstallLocation = EditInstallLocation,
            InstallSiteName = EditInstallLocation,
            DepositText = EditDepositText,
            MonthlyFee = EditMonthlyFee,
            ContractMonths = EditContractMonths,
            ContractDate = ToDateOnly(EditContractDate),
            InstallDate = ToDateOnly(EditInstallDate),
            ContractStartDate = ToDateOnly(EditContractStartDate),
            RentalEndDate = ToDateOnly(EditRentalEndDate),
            FreeSupplyItems = EditFreeSupplyItems,
            PaidSupplyItems = EditPaidSupplyItems,
            ResponsibleOfficeCode = documentOfficeCode,
            AssetStatus = EditAssetStatus,
            Notes = EditNotes
        };

        return true;
    }

    private async Task<LocalCustomer?> ResolveDocumentCustomerAsync(LocalRentalAsset asset)
    {
        if (asset.CustomerId is Guid customerId && customerId != Guid.Empty)
        {
            var byId = await _local.GetCustomerForRentalScopeAsync(customerId, _session);
            if (byId is not null)
                return byId;
        }

        if (string.IsNullOrWhiteSpace(asset.CustomerName))
            return null;

        return (await _local.GetCustomersForRentalScopeAsync(_session))
            .FirstOrDefault(current =>
                string.Equals((current.NameOriginal ?? string.Empty).Trim(), asset.CustomerName.Trim(), StringComparison.CurrentCultureIgnoreCase));
    }

    private static void MergeCurrentDocumentAsset(LocalRentalAsset currentAsset, IList<LocalRentalAsset> assets)
    {
        var index = assets
            .Select((asset, position) => new { asset, position })
            .FirstOrDefault(entry =>
                entry.asset.Id == currentAsset.Id ||
                (!string.IsNullOrWhiteSpace(currentAsset.ManagementNumber) &&
                 string.Equals(entry.asset.ManagementNumber, currentAsset.ManagementNumber, StringComparison.CurrentCultureIgnoreCase)))
            ?.position;

        if (index.HasValue)
            assets[index.Value] = currentAsset;
        else
            assets.Insert(0, currentAsset);
    }

    private async Task<IReadOnlyDictionary<string, string>> BuildManagementCompanyNameLookupAsync()
    {
        var companies = await _rental.GetManagementCompaniesAsync();
        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var company in companies)
        {
            var code = (company.Code ?? string.Empty).Trim();
            var name = string.IsNullOrWhiteSpace(company.Name) ? code : company.Name.Trim();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
                continue;

            lookup[code] = name;
            if (OfficeCodeCatalog.TryNormalizeOfficeCode(code, out var normalizedCode))
                lookup[normalizedCode] = name;
        }

        return lookup;
    }

    private static string BuildDefaultReturnReason(LocalRentalAsset asset)
    {
        if (string.Equals(asset.AssetStatus, "폐기", StringComparison.OrdinalIgnoreCase))
            return "폐기 예정";
        if (string.Equals(asset.AssetStatus, "회수", StringComparison.OrdinalIgnoreCase))
            return "계약 종료 또는 회수 처리";
        if (string.Equals(asset.AssetStatus, "대기", StringComparison.OrdinalIgnoreCase))
            return "재배치 또는 보류";
        return string.Empty;
    }

    private string ResolveManagementCompanyCodeForCurrentAsset(string fallbackOfficeCode)
    {
        var source = SelectedRow?.Source;
        if (source is not null &&
            source.Id == EditId &&
            !string.IsNullOrWhiteSpace(source.ManagementCompanyCode))
        {
            return source.ManagementCompanyCode.Trim();
        }

        return fallbackOfficeCode;
    }

    private void OpenPreview(FixedDocument document, string jobName)
    {
        var previewViewModel = new PrintPreviewViewModel(document, _printService, jobName);
        var previewWindow = new PrintPreviewWindow(previewViewModel)
        {
            Owner = GetActiveWindow()
        };
        previewWindow.ShowDialog();
    }

    public async Task<IReadOnlyList<LookupRow>> BuildCustomerLookupRowsAsync()
    {
        var customers = await _local.GetCustomersForRentalScopeAsync(_session);
        return customers
            .OrderBy(customer => customer.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .Select(customer => new LookupRow
            {
                Id = customer.Id,
                PrimaryText = customer.NameOriginal,
                SecondaryText = string.Join(" | ", new[]
                {
                    customer.BusinessNumber,
                    customer.Phone,
                    customer.Address
                }.Where(value => !string.IsNullOrWhiteSpace(value))),
                Tag = customer
            })
            .ToList();
    }

    public async Task<IReadOnlyList<LookupRow>> BuildItemLookupRowsAsync()
    {
        var items = (await _local.GetItemsForRentalScopeAsync(_session))
            .Where(item => ItemOperationalPolicy.IsAsset(item.TrackingType))
            .ToList();
        return items
            .OrderBy(item => item.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => new LookupRow
            {
                Id = item.Id,
                PrimaryText = item.NameOriginal,
                SecondaryText = string.Join(" | ", new[]
                {
                    item.SpecificationOriginal,
                    item.MaterialNumber,
                    item.SerialNumber
                }.Where(value => !string.IsNullOrWhiteSpace(value))),
                Tag = item
            })
            .ToList();
    }

    public async Task<IReadOnlyList<LookupRow>> BuildPurchaseVendorLookupRowsAsync()
    {
        var customers = await _local.GetCustomersForRentalScopeAsync(_session);
        return customers
            .OrderBy(customer => customer.NameOriginal, StringComparer.CurrentCultureIgnoreCase)
            .Select(customer => new LookupRow
            {
                Id = customer.Id,
                PrimaryText = customer.NameOriginal,
                SecondaryText = string.Join(" | ", new[]
                {
                    customer.BusinessNumber,
                    customer.Phone
                }.Where(value => !string.IsNullOrWhiteSpace(value))),
                Tag = customer
            })
            .ToList();
    }

    public void ApplySelectedCustomer(LocalCustomer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        EditCustomerId = customer.Id;
        EditCustomerName = customer.NameOriginal?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(EditInstallLocation))
        {
            EditInstallLocation = string.Join(" ", new[] { customer.Address, customer.DetailAddress }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }
    }

    public async Task ApplySelectedItemAsync(LocalItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        EditItemId = item.Id;
        EditItemName = item.NameOriginal?.Trim() ?? string.Empty;
        EditItemCategoryName = item.CategoryName?.Trim() ?? string.Empty;
        if (EditPurchasePrice <= 0m && item.PurchasePrice > 0m)
            EditPurchasePrice = item.PurchasePrice;
        if (EditSalePrice <= 0m && item.SalePrice > 0m)
            EditSalePrice = item.SalePrice;
        if (string.IsNullOrWhiteSpace(EditCurrentLocation))
            EditCurrentLocation = item.StorageLocation?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(EditInstallLocation) && !string.IsNullOrWhiteSpace(item.InstallLocation))
            EditInstallLocation = item.InstallLocation.Trim();
        if (string.IsNullOrWhiteSpace(EditMachineNumber) && !string.IsNullOrWhiteSpace(item.SerialNumber))
            EditMachineNumber = item.SerialNumber.Trim();

        var vendorName = await _local.GetLatestPurchaseVendorNameAsync(item.Id);
        if (!string.IsNullOrWhiteSpace(vendorName))
            EditPurchaseVendor = vendorName;
    }

    public void ApplySelectedPurchaseVendor(LocalCustomer customer)
    {
        ArgumentNullException.ThrowIfNull(customer);
        EditPurchaseVendor = customer.NameOriginal?.Trim() ?? string.Empty;
    }

    private async Task ReloadFiltersAsync()
    {
        var offices = await _local.GetOfficesAsync();
        var readableOfficeCodes = _rental.GetReadableAssetOfficeCodes(_session);
        var writableOfficeCodes = _rental.GetWritableAssetOfficeCodes(_session);
        var currentFilterValues = GetSelectedFilterValues(OfficeFilterOptions);
        var currentEditOfficeCode = EditOfficeCode;
        var selectedRowOfficeCode = ResolveAssetOfficeCode(SelectedRow?.Source, _session.OfficeCode);

        _suppressFilterReload = true;
        try
        {
            var readableOfficeOptions = BuildOfficeDisplayOptions(offices, readableOfficeCodes);
            ResetSelectableFilterOptions(
                OfficeFilterOptions,
                readableOfficeOptions.Select(office => new SelectableFilterOption(office.Value, office.DisplayName)),
                currentFilterValues);

            EditOfficeOptions.Clear();

            var editableOfficeCodes = BuildEditableAssetOfficeCodes(
                writableOfficeCodes,
                readableOfficeCodes,
                [currentEditOfficeCode, selectedRowOfficeCode]);
            foreach (var office in BuildOfficeDisplayOptions(offices, editableOfficeCodes))
            {
                EditOfficeOptions.Add(new DisplayOption
                {
                    Value = office.Value,
                    DisplayName = office.DisplayName
                });
            }

            if (EditOfficeOptions.Count == 0)
            {
                var fallbackOfficeCode = _rental.GetDefaultAssetOfficeCode(_session);
                var fallbackDisplayName = OfficeCodeCatalog.GetOfficeDisplayName(fallbackOfficeCode);
                EditOfficeOptions.Add(new DisplayOption
                {
                    Value = fallbackOfficeCode,
                    DisplayName = fallbackDisplayName
                });
                if (OfficeFilterOptions.All(option => !string.Equals(option.Value, fallbackOfficeCode, StringComparison.OrdinalIgnoreCase)))
                    OfficeFilterOptions.Add(CreateFilterOption(fallbackOfficeCode, fallbackDisplayName, currentFilterValues));
            }

            EditOfficeCode = EditOfficeOptions.FirstOrDefault(option =>
                               string.Equals(option.Value, currentEditOfficeCode, StringComparison.OrdinalIgnoreCase))?.Value
                           ?? EditOfficeOptions.First().Value;
            OnPropertyChanged(nameof(SelectedOfficeFilterSummary));

        }
        finally
        {
            _suppressFilterReload = false;
        }
    }

    private static IReadOnlyList<string> BuildEditableAssetOfficeCodes(
        IEnumerable<string> writableOfficeCodes,
        IEnumerable<string> readableOfficeCodes,
        IEnumerable<string?> preserveOfficeCodes)
    {
        var editableOfficeCodes = NormalizeOfficeCodes(writableOfficeCodes);
        var readableOfficeCodeSet = NormalizeOfficeCodes(readableOfficeCodes);
        foreach (var officeCode in preserveOfficeCodes)
        {
            if (!OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOfficeCode))
                continue;

            if (readableOfficeCodeSet.Contains(normalizedOfficeCode))
                editableOfficeCodes.Add(normalizedOfficeCode);
        }

        return editableOfficeCodes.ToList();
    }

    private static HashSet<string> NormalizeOfficeCodes(IEnumerable<string?> officeCodes)
    {
        var normalizedOfficeCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var officeCode in officeCodes)
        {
            if (OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOfficeCode))
                normalizedOfficeCodes.Add(normalizedOfficeCode);
        }

        return normalizedOfficeCodes;
    }

    private static IReadOnlyList<DisplayOption> BuildOfficeDisplayOptions(
        IEnumerable<LocalOffice> offices,
        IEnumerable<string> officeCodes)
    {
        var normalizedOfficeCodes = NormalizeOfficeCodes(officeCodes)
            .OrderBy(OfficeCodeCatalog.GetOfficeDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var officeLookup = offices
            .Select(office => new
            {
                Office = office,
                Code = OfficeCodeCatalog.TryNormalizeOfficeCode(office.Code, out var normalizedOfficeCode)
                    ? normalizedOfficeCode
                    : string.Empty
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Code))
            .GroupBy(entry => entry.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Office,
                StringComparer.OrdinalIgnoreCase);

        return normalizedOfficeCodes
            .Select(code =>
            {
                var displayName = officeLookup.TryGetValue(code, out var office) && !string.IsNullOrWhiteSpace(office.Name)
                    ? office.Name
                    : OfficeCodeCatalog.GetOfficeDisplayName(code);
                return new DisplayOption
                {
                    Value = code,
                    DisplayName = displayName
                };
            })
            .OrderBy(option => option.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void EnsureEditOfficeOption(string? officeCode)
    {
        if (!OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var normalizedOfficeCode))
            return;

        if (EditOfficeOptions.Any(option => string.Equals(option.Value, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase)))
            return;

        var readableOfficeCodes = _rental.GetReadableAssetOfficeCodes(_session);
        if (!readableOfficeCodes.Contains(normalizedOfficeCode))
            return;

        EditOfficeOptions.Add(new DisplayOption
        {
            Value = normalizedOfficeCode,
            DisplayName = OfficeCodeCatalog.GetOfficeDisplayName(normalizedOfficeCode)
        });
    }

    private static string ResolveAssetOfficeCode(LocalRentalAsset? asset, string fallbackOfficeCode)
    {
        if (asset is null)
            return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(fallbackOfficeCode, DomainConstants.OfficeUsenet);

        return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            string.IsNullOrWhiteSpace(asset.ResponsibleOfficeCode)
                ? asset.ManagementCompanyCode
                : asset.ResponsibleOfficeCode,
            fallbackOfficeCode);
    }

    private void HandleInventoryStateChanged(object? sender, EventArgs e)
        => UiTaskHelper.Forget(ReloadItemCategoryOptionsAsync(), "UI", "렌탈 자산 품목분류 목록 새로고침");

    private async Task ReloadItemCategoryOptionsAsync()
    {
        var currentFilterValues = GetSelectedFilterValues(ItemCategoryFilterOptions);
        var currentValue = EditItemCategoryName;
        _suppressFilterReload = true;
        try
        {
            ItemCategoryOptions.Clear();
            var filterOptions = new List<SelectableFilterOption>();
            foreach (var option in await _local.GetItemCategoryOptionsAsync())
            {
                ItemCategoryOptions.Add(option);
                if (!string.IsNullOrWhiteSpace(option.Name) &&
                    filterOptions.All(current => !string.Equals(current.Value, option.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    filterOptions.Add(new SelectableFilterOption(option.Name, option.Name));
                }
            }

            ResetSelectableFilterOptions(ItemCategoryFilterOptions, filterOptions, currentFilterValues);
            OnPropertyChanged(nameof(SelectedItemCategoryFilterSummary));

            if (!string.IsNullOrWhiteSpace(currentValue))
            {
                var matched = ItemCategoryOptions.FirstOrDefault(option =>
                    string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name), RentalCatalogValueNormalizer.NormalizeLooseKey(currentValue), StringComparison.OrdinalIgnoreCase));
                if (matched is not null)
                {
                    EditItemCategoryName = matched.Name;
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(SelectedRow?.Source.ItemCategoryName) && string.IsNullOrWhiteSpace(EditItemCategoryName))
                EditItemCategoryName = ItemCategoryOptions.FirstOrDefault()?.Name ?? string.Empty;
        }
        finally
        {
            _suppressFilterReload = false;
        }
    }

    private void SelectRow(Guid entityId)
    {
        SelectedRow = Rows.FirstOrDefault(row => row.Source.Id == entityId);
    }

    [RelayCommand]
    private void SelectAllOfficeFilters() => SetAllFilterSelections(OfficeFilterOptions, true);

    [RelayCommand]
    private void ClearOfficeFilters() => SetAllFilterSelections(OfficeFilterOptions, false);

    [RelayCommand]
    private void SelectAllItemCategoryFilters() => SetAllFilterSelections(ItemCategoryFilterOptions, true);

    [RelayCommand]
    private void ClearItemCategoryFilters() => SetAllFilterSelections(ItemCategoryFilterOptions, false);

    [RelayCommand]
    private void SelectAllStatusFilters() => SetAllFilterSelections(StatusFilterOptions, true);

    [RelayCommand]
    private void ClearStatusFilters() => SetAllFilterSelections(StatusFilterOptions, false);

    private void HandleFilterOptionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(SelectableFilterOption.IsSelected), StringComparison.Ordinal))
            return;

        OnPropertyChanged(nameof(SelectedOfficeFilterSummary));
        OnPropertyChanged(nameof(SelectedItemCategoryFilterSummary));
        OnPropertyChanged(nameof(SelectedStatusFilterSummary));
        RequestFilterReload();
    }

    private void ResetSelectableFilterOptions(
        ObservableCollection<SelectableFilterOption> target,
        IEnumerable<SelectableFilterOption> source,
        IReadOnlyCollection<string>? selectedValues = null)
    {
        foreach (var option in target)
            option.PropertyChanged -= HandleFilterOptionPropertyChanged;

        var normalizedSelectedValues = (selectedValues ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        target.Clear();
        foreach (var option in source)
        {
            var next = new SelectableFilterOption(
                option.Value,
                option.DisplayName,
                normalizedSelectedValues.Count == 0 || normalizedSelectedValues.Contains(option.Value));
            next.PropertyChanged += HandleFilterOptionPropertyChanged;
            target.Add(next);
        }
    }

    private static SelectableFilterOption CreateFilterOption(
        string value,
        string displayName,
        IReadOnlyCollection<string>? selectedValues = null)
    {
        var isSelected = selectedValues is null ||
                         selectedValues.Count == 0 ||
                         selectedValues.Contains(value, StringComparer.OrdinalIgnoreCase);
        return new SelectableFilterOption(value, displayName, isSelected);
    }

    private static List<string> GetSelectedFilterValues(IEnumerable<SelectableFilterOption> options)
        => options
            .Where(option => option.IsSelected)
            .Select(option => option.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private void SetSelectedFilterValues(ObservableCollection<SelectableFilterOption> options, IReadOnlyCollection<string> selectedValues)
    {
        _suppressFilterReload = true;
        try
        {
            var normalizedValues = selectedValues
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var option in options)
                option.IsSelected = normalizedValues.Count == 0 || normalizedValues.Contains(option.Value);
        }
        finally
        {
            _suppressFilterReload = false;
        }

        OnPropertyChanged(nameof(SelectedOfficeFilterSummary));
        OnPropertyChanged(nameof(SelectedItemCategoryFilterSummary));
        OnPropertyChanged(nameof(SelectedStatusFilterSummary));
    }

    private void SetAllFilterSelections(ObservableCollection<SelectableFilterOption> options, bool isSelected)
    {
        _suppressFilterReload = true;
        try
        {
            foreach (var option in options)
                option.IsSelected = isSelected;
        }
        finally
        {
            _suppressFilterReload = false;
        }

        OnPropertyChanged(nameof(SelectedOfficeFilterSummary));
        OnPropertyChanged(nameof(SelectedItemCategoryFilterSummary));
        OnPropertyChanged(nameof(SelectedStatusFilterSummary));
        RequestFilterReload();
    }

    private static string BuildFilterSummary(IEnumerable<SelectableFilterOption> options, string fallbackLabel)
    {
        var list = options.ToList();
        if (list.Count == 0)
            return $"{fallbackLabel} 전체";

        var selected = list.Where(option => option.IsSelected).ToList();
        if (selected.Count == 0 || selected.Count == list.Count)
            return $"{fallbackLabel} 전체";

        if (selected.Count == 1)
            return selected[0].DisplayName;

        return $"{fallbackLabel} {selected.Count}개";
    }

    private void RequestFilterReload()
    {
        if (_suppressFilterReload)
            return;

        _searchDebouncer.DebounceAsync(
            TimeSpan.FromMilliseconds(350),
            () => ReloadAsync(),
            ex => StatusMessage = $"렌탈 자산 목록을 다시 불러오지 못했습니다. {ex.Message}");
    }

    private static DateOnly? ToDateOnly(DateTime? value)
        => value.HasValue ? DateOnly.FromDateTime(value.Value) : null;

    private static DateTime? ToDateTime(DateOnly? value)
        => value?.ToDateTime(TimeOnly.MinValue);

    private static Window? GetActiveWindow()
        => Application.Current?.Windows
            .OfType<Window>()
            .FirstOrDefault(window => window.IsActive);

    private static string BuildSafeDocumentSuffix(LocalRentalAsset asset)
    {
        var candidate = string.IsNullOrWhiteSpace(asset.CustomerName)
            ? Coalesce(asset.ManagementNumber, asset.ManagementId, asset.ItemName)
            : asset.CustomerName;
        return string.Concat((candidate ?? string.Empty).Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)));
    }

    private static string Coalesce(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "문서";

    public async Task<bool> TryAutoSaveOnCloseAsync()
        => await TryAutoSaveCurrentEditAsync(refreshAfterSave: false);

    private async Task<bool> HandleSelectionAutoSaveAsync(
        RentalAssetEditSnapshot snapshot,
        RentalAssetViewRow? previousSelection,
        RentalAssetViewRow? requestedSelection)
    {
        var saved = await SaveSnapshotAsync(
            snapshot,
            preserveSelectionRowId: requestedSelection?.Source.Id,
            refreshAfterSave: true,
            successMessage: "렌탈 자산을 자동 저장했습니다.",
            permissionDeniedMessage: "현재 선택한 렌탈 자산을 자동 저장할 권한이 없습니다.",
            showConflictDialog: false);

        if (saved)
            return true;

        RestoreEditSnapshot(previousSelection, snapshot);
        StatusMessage = string.IsNullOrWhiteSpace(StatusMessage)
            ? "자동저장에 실패해 기존 편집 내용을 유지했습니다."
            : $"{StatusMessage} 기존 편집 내용은 유지했습니다.";
        return false;
    }

    private async Task<bool> TryAutoSaveCurrentEditAsync(bool refreshAfterSave)
    {
        if (!TryCaptureAutoSaveSnapshot(out var snapshot))
            return true;

        return await SaveSnapshotAsync(
            snapshot,
            preserveSelectionRowId: SelectedRow?.Source.Id,
            refreshAfterSave: refreshAfterSave,
            successMessage: "렌탈 자산을 자동 저장했습니다.",
            permissionDeniedMessage: "현재 선택한 렌탈 자산을 자동 저장할 권한이 없습니다.",
            showConflictDialog: false);
    }

    private bool TryCaptureAutoSaveSnapshot(out RentalAssetEditSnapshot snapshot)
    {
        snapshot = CaptureEditSnapshot();
        return HasPendingChanges
               && HasMeaningfulDraftContent(snapshot)
               && CanSave;
    }

    private async Task<bool> SaveSnapshotAsync(
        RentalAssetEditSnapshot snapshot,
        Guid? preserveSelectionRowId,
        bool refreshAfterSave,
        string successMessage,
        string permissionDeniedMessage,
        bool showConflictDialog)
    {
        await _autoSaveGate.WaitAsync();
        try
        {
            if (!CanSave)
            {
                StatusMessage = permissionDeniedMessage;
                return false;
            }

            var result = await _rental.SaveAssetAsync(BuildAsset(snapshot), _session);
            if (!result.Success)
            {
                StatusMessage = result.Message;
                if (result.ConcurrencyConflict && showConflictDialog)
                {
                    MessageBox.Show(
                        result.Message,
                        "동시 수정 충돌",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return false;
            }

            if (refreshAfterSave)
            {
                var selectionIdBeforeRefresh = preserveSelectionRowId ?? SelectedRow?.Source.Id;
                _suppressSelectionAutoSave = true;
                try
                {
                    await ReloadAsync();
                    if (selectionIdBeforeRefresh.HasValue)
                        SelectedRow = Rows.FirstOrDefault(row => row.Source.Id == selectionIdBeforeRefresh.Value);
                }
                finally
                {
                    _suppressSelectionAutoSave = false;
                }
            }

            StatusMessage = successMessage;
            return true;
        }
        finally
        {
            _autoSaveGate.Release();
        }
    }

    private void ResetForNewAsset(string? statusMessage = null)
    {
        SelectRowWithoutAutoSave(null);
        ApplySnapshot(CreateEmptySnapshot(), resetBaseline: true);
        if (!string.IsNullOrWhiteSpace(statusMessage))
            StatusMessage = statusMessage;
    }

    private void SelectRowWithoutAutoSave(Guid? rowId)
    {
        _suppressSelectionAutoSave = true;
        try
        {
            SelectedRow = rowId.HasValue
                ? Rows.FirstOrDefault(row => row.Source.Id == rowId.Value)
                : null;
        }
        finally
        {
            _suppressSelectionAutoSave = false;
        }
    }

    private void RestoreEditSnapshot(RentalAssetViewRow? previousSelection, RentalAssetEditSnapshot snapshot)
    {
        SelectRowWithoutAutoSave(previousSelection?.Source.Id);
        ApplySnapshot(snapshot, resetBaseline: false);
    }

    private RentalAssetEditSnapshot CaptureEditSnapshot()
        => new(
            EditId,
            _editRevision,
            EditCustomerId,
            EditItemId,
            EditManagementId,
            EditManagementNumber,
            EditOfficeCode,
            EditCurrentLocation,
            EditItemCategoryName,
            EditManufacturer,
            EditItemName,
            EditMachineNumber,
            EditPurchaseVendor,
            EditPurchasePrice,
            EditSalePrice,
            EditCustomerName,
            EditCurrentCustomerName,
            EditInstallLocation,
            EditLastCustomerName,
            EditLastInstallLocation,
            EditLastBillingProfileDisplay,
            EditLastAssignmentClearedAtText,
            EditDepositText,
            EditMonthlyFee,
            EditContractMonths,
            EditFreeSupplyItems,
            EditPaidSupplyItems,
            EditAssetStatus,
            EditBillingEligibilityStatus,
            EditBillingExclusionReason,
            EditNotes,
            EditPurchaseDate,
            EditDisposalDate,
            EditContractDate,
            EditInstallDate,
            EditContractStartDate,
            EditRentalEndDate,
            IsNewAsset);

    private void ApplySnapshot(RentalAssetEditSnapshot snapshot, bool resetBaseline)
    {
        _editRevision = snapshot.EditRevision;
        EditId = snapshot.EditId;
        EditCustomerId = snapshot.EditCustomerId;
        EditItemId = snapshot.EditItemId;
        EditManagementId = snapshot.EditManagementId;
        EditManagementNumber = snapshot.EditManagementNumber;
        EditOfficeCode = snapshot.EditOfficeCode;
        EnsureEditOfficeOption(EditOfficeCode);
        EditCurrentLocation = snapshot.EditCurrentLocation;
        EditItemCategoryName = snapshot.EditItemCategoryName;
        EditManufacturer = snapshot.EditManufacturer;
        EditItemName = snapshot.EditItemName;
        EditMachineNumber = snapshot.EditMachineNumber;
        EditPurchaseVendor = snapshot.EditPurchaseVendor;
        EditPurchasePrice = snapshot.EditPurchasePrice;
        EditSalePrice = snapshot.EditSalePrice;
        EditCustomerName = snapshot.EditCustomerName;
        EditCurrentCustomerName = snapshot.EditCurrentCustomerName;
        EditInstallLocation = snapshot.EditInstallLocation;
        EditLastCustomerName = snapshot.EditLastCustomerName;
        EditLastInstallLocation = snapshot.EditLastInstallLocation;
        EditLastBillingProfileDisplay = snapshot.EditLastBillingProfileDisplay;
        EditLastAssignmentClearedAtText = snapshot.EditLastAssignmentClearedAtText;
        EditDepositText = snapshot.EditDepositText;
        EditMonthlyFee = snapshot.EditMonthlyFee;
        EditContractMonths = snapshot.EditContractMonths;
        EditFreeSupplyItems = snapshot.EditFreeSupplyItems;
        EditPaidSupplyItems = snapshot.EditPaidSupplyItems;
        EditAssetStatus = snapshot.EditAssetStatus;
        EditBillingEligibilityStatus = snapshot.EditBillingEligibilityStatus;
        EditBillingExclusionReason = snapshot.EditBillingExclusionReason;
        EditNotes = snapshot.EditNotes;
        EditPurchaseDate = snapshot.EditPurchaseDate;
        EditDisposalDate = snapshot.EditDisposalDate;
        EditContractDate = snapshot.EditContractDate;
        EditInstallDate = snapshot.EditInstallDate;
        EditContractStartDate = snapshot.EditContractStartDate;
        EditRentalEndDate = snapshot.EditRentalEndDate;

        ApplyAssetStatusUiRules();
        OnPropertyChanged(nameof(IsNewAsset));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(HasLastAssignmentHistory));
        OnPropertyChanged(nameof(ShowLastAssignmentHistory));
        OnPropertyChanged(nameof(IsNonOperatingAssetStatus));
        OnPropertyChanged(nameof(CanEditAssignmentFields));
        OnPropertyChanged(nameof(AssignmentFieldsNotice));

        if (resetBaseline)
            ResetEditBaseline();
    }

    private RentalAssetEditSnapshot CreateEmptySnapshot()
        => new(
            EditId: Guid.NewGuid(),
            EditRevision: 0,
            EditCustomerId: null,
            EditItemId: null,
            EditManagementId: string.Empty,
            EditManagementNumber: string.Empty,
            EditOfficeCode: EditOfficeOptions.FirstOrDefault()?.Value ?? _rental.GetDefaultAssetOfficeCode(_session),
            EditCurrentLocation: string.Empty,
            EditItemCategoryName: ItemCategoryOptions.FirstOrDefault()?.Name ?? string.Empty,
            EditManufacturer: string.Empty,
            EditItemName: string.Empty,
            EditMachineNumber: string.Empty,
            EditPurchaseVendor: string.Empty,
            EditPurchasePrice: 0m,
            EditSalePrice: 0m,
            EditCustomerName: string.Empty,
            EditCurrentCustomerName: string.Empty,
            EditInstallLocation: string.Empty,
            EditLastCustomerName: string.Empty,
            EditLastInstallLocation: string.Empty,
            EditLastBillingProfileDisplay: string.Empty,
            EditLastAssignmentClearedAtText: string.Empty,
            EditDepositText: string.Empty,
            EditMonthlyFee: 0m,
            EditContractMonths: 0,
            EditFreeSupplyItems: string.Empty,
            EditPaidSupplyItems: string.Empty,
            EditAssetStatus: "임대진행중",
            EditBillingEligibilityStatus: "미확인",
            EditBillingExclusionReason: string.Empty,
            EditNotes: string.Empty,
            EditPurchaseDate: null,
            EditDisposalDate: null,
            EditContractDate: null,
            EditInstallDate: null,
            EditContractStartDate: null,
            EditRentalEndDate: null,
            IsNewAsset: true);

    private LocalRentalAsset BuildAsset(RentalAssetEditSnapshot snapshot)
    {
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            snapshot.EditOfficeCode,
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet));
        var managementCompanyCode = ResolveManagementCompanyCodeForCurrentAsset(officeCode);

        return new LocalRentalAsset
        {
            Id = snapshot.EditId,
            Revision = snapshot.EditRevision,
            TenantCode = TenantScopeCatalog.GetTenantCodeForOffice(officeCode),
            OfficeCode = officeCode,
            CustomerId = snapshot.EditCustomerId,
            ItemId = snapshot.EditItemId,
            ManagementId = snapshot.EditManagementId,
            ManagementNumber = snapshot.EditManagementNumber,
            ManagementCompanyCode = managementCompanyCode,
            CurrentLocation = snapshot.EditCurrentLocation,
            ItemCategoryName = snapshot.EditItemCategoryName,
            Manufacturer = snapshot.EditManufacturer,
            ItemName = snapshot.EditItemName,
            MachineNumber = snapshot.EditMachineNumber,
            PurchaseVendor = snapshot.EditPurchaseVendor,
            PurchasePrice = snapshot.EditPurchasePrice,
            SalePrice = snapshot.EditSalePrice,
            CustomerName = snapshot.EditCustomerName,
            CurrentCustomerName = snapshot.EditCurrentCustomerName,
            InstallLocation = snapshot.EditInstallLocation,
            InstallSiteName = snapshot.EditInstallLocation,
            DepositText = snapshot.EditDepositText,
            MonthlyFee = snapshot.EditMonthlyFee,
            ContractMonths = snapshot.EditContractMonths,
            FreeSupplyItems = snapshot.EditFreeSupplyItems,
            PaidSupplyItems = snapshot.EditPaidSupplyItems,
            ResponsibleOfficeCode = officeCode,
            AssetStatus = snapshot.EditAssetStatus,
            BillingEligibilityStatus = snapshot.EditBillingEligibilityStatus,
            BillingExclusionReason = snapshot.EditBillingExclusionReason,
            Notes = snapshot.EditNotes,
            PurchaseDate = ToDateOnly(snapshot.EditPurchaseDate),
            DisposalDate = ToDateOnly(snapshot.EditDisposalDate),
            ContractDate = ToDateOnly(snapshot.EditContractDate),
            InstallDate = ToDateOnly(snapshot.EditInstallDate),
            ContractStartDate = ToDateOnly(snapshot.EditContractStartDate),
            RentalEndDate = ToDateOnly(snapshot.EditRentalEndDate)
        };
    }

    private void ResetEditBaseline()
        => _baselineStateSignature = BuildEditStateSignature(CaptureEditSnapshot());

    private static bool HasMeaningfulDraftContent(RentalAssetEditSnapshot snapshot)
        => !string.IsNullOrWhiteSpace(snapshot.EditCustomerName)
           || !string.IsNullOrWhiteSpace(snapshot.EditItemName)
           || !string.IsNullOrWhiteSpace(snapshot.EditInstallLocation)
           || !string.IsNullOrWhiteSpace(snapshot.EditCurrentLocation)
           || !string.IsNullOrWhiteSpace(snapshot.EditManagementNumber)
           || !string.IsNullOrWhiteSpace(snapshot.EditManagementId)
           || !string.IsNullOrWhiteSpace(snapshot.EditManufacturer)
           || !string.IsNullOrWhiteSpace(snapshot.EditMachineNumber)
           || !string.IsNullOrWhiteSpace(snapshot.EditPurchaseVendor)
           || !string.IsNullOrWhiteSpace(snapshot.EditDepositText)
           || !string.IsNullOrWhiteSpace(snapshot.EditNotes)
           || !string.IsNullOrWhiteSpace(snapshot.EditFreeSupplyItems)
           || !string.IsNullOrWhiteSpace(snapshot.EditPaidSupplyItems)
           || snapshot.EditPurchasePrice != 0m
           || snapshot.EditSalePrice != 0m
           || snapshot.EditMonthlyFee != 0m
           || snapshot.EditContractMonths != 0
           || snapshot.EditPurchaseDate.HasValue
           || snapshot.EditDisposalDate.HasValue
           || snapshot.EditContractDate.HasValue
           || snapshot.EditInstallDate.HasValue
           || snapshot.EditContractStartDate.HasValue
           || snapshot.EditRentalEndDate.HasValue;

    private static string BuildEditStateSignature(RentalAssetEditSnapshot snapshot)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(snapshot.EditId.ToString("D"))
            .Append('|').Append(snapshot.EditCustomerId?.ToString("D") ?? string.Empty)
            .Append('|').Append(snapshot.EditItemId?.ToString("D") ?? string.Empty)
            .Append('|').Append(snapshot.EditManagementId ?? string.Empty)
            .Append('|').Append(snapshot.EditManagementNumber ?? string.Empty)
            .Append('|').Append(snapshot.EditOfficeCode ?? string.Empty)
            .Append('|').Append(snapshot.EditCurrentLocation ?? string.Empty)
            .Append('|').Append(snapshot.EditItemCategoryName ?? string.Empty)
            .Append('|').Append(snapshot.EditManufacturer ?? string.Empty)
            .Append('|').Append(snapshot.EditItemName ?? string.Empty)
            .Append('|').Append(snapshot.EditMachineNumber ?? string.Empty)
            .Append('|').Append(snapshot.EditPurchaseVendor ?? string.Empty)
            .Append('|').Append(snapshot.EditPurchasePrice)
            .Append('|').Append(snapshot.EditSalePrice)
            .Append('|').Append(snapshot.EditCustomerName ?? string.Empty)
            .Append('|').Append(snapshot.EditCurrentCustomerName ?? string.Empty)
            .Append('|').Append(snapshot.EditInstallLocation ?? string.Empty)
            .Append('|').Append(snapshot.EditLastCustomerName ?? string.Empty)
            .Append('|').Append(snapshot.EditLastInstallLocation ?? string.Empty)
            .Append('|').Append(snapshot.EditLastBillingProfileDisplay ?? string.Empty)
            .Append('|').Append(snapshot.EditLastAssignmentClearedAtText ?? string.Empty)
            .Append('|').Append(snapshot.EditDepositText ?? string.Empty)
            .Append('|').Append(snapshot.EditMonthlyFee)
            .Append('|').Append(snapshot.EditContractMonths)
            .Append('|').Append(snapshot.EditFreeSupplyItems ?? string.Empty)
            .Append('|').Append(snapshot.EditPaidSupplyItems ?? string.Empty)
            .Append('|').Append(snapshot.EditAssetStatus ?? string.Empty)
            .Append('|').Append(snapshot.EditBillingEligibilityStatus ?? string.Empty)
            .Append('|').Append(snapshot.EditBillingExclusionReason ?? string.Empty)
            .Append('|').Append(snapshot.EditNotes ?? string.Empty)
            .Append('|').Append(snapshot.EditPurchaseDate?.ToString("yyyy-MM-dd") ?? string.Empty)
            .Append('|').Append(snapshot.EditDisposalDate?.ToString("yyyy-MM-dd") ?? string.Empty)
            .Append('|').Append(snapshot.EditContractDate?.ToString("yyyy-MM-dd") ?? string.Empty)
            .Append('|').Append(snapshot.EditInstallDate?.ToString("yyyy-MM-dd") ?? string.Empty)
            .Append('|').Append(snapshot.EditContractStartDate?.ToString("yyyy-MM-dd") ?? string.Empty)
            .Append('|').Append(snapshot.EditRentalEndDate?.ToString("yyyy-MM-dd") ?? string.Empty)
            .Append('|').Append(snapshot.IsNewAsset);
        return builder.ToString();
    }

    private sealed record RentalAssetEditSnapshot(
        Guid EditId,
        long EditRevision,
        Guid? EditCustomerId,
        Guid? EditItemId,
        string EditManagementId,
        string EditManagementNumber,
        string EditOfficeCode,
        string EditCurrentLocation,
        string EditItemCategoryName,
        string EditManufacturer,
        string EditItemName,
        string EditMachineNumber,
        string EditPurchaseVendor,
        decimal EditPurchasePrice,
        decimal EditSalePrice,
        string EditCustomerName,
        string EditCurrentCustomerName,
        string EditInstallLocation,
        string EditLastCustomerName,
        string EditLastInstallLocation,
        string EditLastBillingProfileDisplay,
        string EditLastAssignmentClearedAtText,
        string EditDepositText,
        decimal EditMonthlyFee,
        int EditContractMonths,
        string EditFreeSupplyItems,
        string EditPaidSupplyItems,
        string EditAssetStatus,
        string EditBillingEligibilityStatus,
        string EditBillingExclusionReason,
        string EditNotes,
        DateTime? EditPurchaseDate,
        DateTime? EditDisposalDate,
        DateTime? EditContractDate,
        DateTime? EditInstallDate,
        DateTime? EditContractStartDate,
        DateTime? EditRentalEndDate,
        bool IsNewAsset);
}
