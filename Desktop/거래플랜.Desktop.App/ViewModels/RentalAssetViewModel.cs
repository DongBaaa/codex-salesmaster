using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
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

    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private DisplayOption? _selectedOfficeFilter;
    [ObservableProperty] private string _selectedAssignedUsernameFilter = AllOption;
    [ObservableProperty] private string _selectedStatusFilter = AllOption;
    [ObservableProperty] private DateOnly _referenceDate = DateOnly.FromDateTime(DateTime.Today);
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "렌탈 자산을 불러오는 중입니다.";
    [ObservableProperty] private RentalAssetViewRow? _selectedRow;

    [ObservableProperty] private Guid _editId = Guid.NewGuid();
    [ObservableProperty] private Guid? _editCustomerId;
    [ObservableProperty] private Guid? _editItemId;
    [ObservableProperty] private string _editManagementId = string.Empty;
    [ObservableProperty] private string _editManagementNumber = string.Empty;
    [ObservableProperty] private string _editOfficeCode = string.Empty;
    [ObservableProperty] private string _editCurrentLocation = string.Empty;
    [ObservableProperty] private string _editProductCategory = string.Empty;
    [ObservableProperty] private string _editManufacturer = string.Empty;
    [ObservableProperty] private string _editModelName = string.Empty;
    [ObservableProperty] private string _editMachineNumber = string.Empty;
    [ObservableProperty] private string _editPurchaseVendor = string.Empty;
    [ObservableProperty] private decimal _editPurchasePrice;
    [ObservableProperty] private decimal _editSalePrice;
    [ObservableProperty] private string _editCustomerName = string.Empty;
    [ObservableProperty] private string _editInstallLocation = string.Empty;
    [ObservableProperty] private string _editDepositText = string.Empty;
    [ObservableProperty] private decimal _editMonthlyFee;
    [ObservableProperty] private int _editContractMonths;
    [ObservableProperty] private string _editFreeSupplyItems = string.Empty;
    [ObservableProperty] private string _editPaidSupplyItems = string.Empty;
    [ObservableProperty] private string _editAssignedUsername = string.Empty;
    [ObservableProperty] private string _editAssetStatus = "임대진행중";
    [ObservableProperty] private string _editNotes = string.Empty;
    [ObservableProperty] private DateTime? _editPurchaseDate;
    [ObservableProperty] private DateTime? _editDisposalDate;
    [ObservableProperty] private DateTime? _editContractDate;
    [ObservableProperty] private DateTime? _editInstallDate;
    [ObservableProperty] private DateTime? _editContractStartDate;
    [ObservableProperty] private DateTime? _editRentalEndDate;

    public ObservableCollection<DisplayOption> OfficeOptions { get; } = new();
    public ObservableCollection<string> AssignedUsernameOptions { get; } = new();
    public ObservableCollection<string> AssetStatusOptions { get; } = new();
    public ObservableCollection<RentalAssetViewRow> Rows { get; } = new();

    public bool CanViewAll => _session.HasGlobalDataScope ||
                              _session.HasAssignedPermission(AppPermissionNames.RentalViewAll) ||
                              _session.HasAssignedPermission(AppPermissionNames.RentalEditAll);
    public bool CanManageAll => _session.HasAdministrativePrivileges || _session.HasPermission(AppPermissionNames.RentalEditAll);
    public bool CanSave => SelectedRow is null || CanEditCurrentSelection;
    public bool CanDeleteSelected => SelectedRow is not null && CanEditCurrentSelection;
    public LocalStateService LocalStateService => _local;
    public SessionState SessionState => _session;

    private bool CanEditCurrentSelection => SelectedRow is null || CanEditScope(SelectedRow.Source.AssignedUsername, SelectedRow.Source.ResponsibleOfficeCode);

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

        AssetStatusOptions.Add(AllOption);
        AssetStatusOptions.Add("임대진행중");
        AssetStatusOptions.Add("대기");
        AssetStatusOptions.Add("회수");
        AssetStatusOptions.Add("폐기");
    }

    public async Task LoadAsync()
    {
        await ReloadFiltersAsync();
        await ReloadAsync();
        NewAsset();
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        IsBusy = true;
        try
        {
            var rows = await _rental.GetAssetRowsAsync(new RentalAssetFilter
            {
                SearchText = SearchText,
                OfficeCode = SelectedOfficeFilter?.Value == AllOption ? string.Empty : SelectedOfficeFilter?.Value ?? string.Empty,
                AssignedUsername = SelectedAssignedUsernameFilter == AllOption ? string.Empty : SelectedAssignedUsernameFilter,
                AssetStatus = SelectedStatusFilter == AllOption ? string.Empty : SelectedStatusFilter,
                ReferenceDate = ReferenceDate
            }, _session);

            Rows.Clear();
            foreach (var row in rows)
                Rows.Add(row);

            StatusMessage = rows.Count == 0
                ? "조건에 맞는 렌탈 자산이 없습니다."
                : $"렌탈 자산 {rows.Count:N0}건을 조회했습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var officeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            EditOfficeCode,
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet));

        var entity = new LocalRentalAsset
        {
            Id = EditId,
            CustomerId = EditCustomerId,
            ItemId = EditItemId,
            ManagementId = EditManagementId,
            ManagementNumber = EditManagementNumber,
            ManagementCompanyCode = officeCode,
            CurrentLocation = EditCurrentLocation,
            ProductCategory = EditProductCategory,
            Manufacturer = EditManufacturer,
            ModelName = EditModelName,
            MachineNumber = EditMachineNumber,
            PurchaseVendor = EditPurchaseVendor,
            PurchasePrice = EditPurchasePrice,
            SalePrice = EditSalePrice,
            CustomerName = EditCustomerName,
            InstallLocation = EditInstallLocation,
            DepositText = EditDepositText,
            MonthlyFee = EditMonthlyFee,
            ContractMonths = EditContractMonths,
            FreeSupplyItems = EditFreeSupplyItems,
            PaidSupplyItems = EditPaidSupplyItems,
            ResponsibleOfficeCode = officeCode,
            AssignedUsername = EditAssignedUsername,
            AssetStatus = EditAssetStatus,
            Notes = EditNotes,
            PurchaseDate = ToDateOnly(EditPurchaseDate),
            DisposalDate = ToDateOnly(EditDisposalDate),
            ContractDate = ToDateOnly(EditContractDate),
            InstallDate = ToDateOnly(EditInstallDate),
            ContractStartDate = ToDateOnly(EditContractStartDate),
            RentalEndDate = ToDateOnly(EditRentalEndDate)
        };

        var result = await _rental.SaveAssetAsync(entity, _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await ReloadAsync();
        SelectRow(result.EntityId);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedRow is null)
        {
            StatusMessage = "삭제할 렌탈 자산을 선택하세요.";
            return;
        }

        var result = await _rental.DeleteAssetAsync(SelectedRow.Source.Id, _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await ReloadAsync();
        NewAsset();
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
        var failureMessages = new List<string>();
        foreach (var row in targets)
        {
            var result = await _rental.DeleteAssetAsync(row.Source.Id, _session);
            if (result.Success)
            {
                successCount++;
                continue;
            }

            failureMessages.Add($"{row.Source.CustomerName}: {result.Message}");
        }

        await ReloadAsync();
        NewAsset();

        StatusMessage = failureMessages.Count == 0
            ? $"선택한 렌탈 자산 {successCount:N0}건을 삭제했습니다."
            : $"삭제 성공 {successCount:N0}건 / 실패 {failureMessages.Count:N0}건 - {string.Join(" | ", failureMessages.Take(3))}";
    }

    [RelayCommand]
    private void NewAsset()
    {
        EditId = Guid.NewGuid();
        EditCustomerId = null;
        EditItemId = null;
        EditManagementId = string.Empty;
        EditManagementNumber = string.Empty;
        EditOfficeCode = OfficeOptions.FirstOrDefault(option => option.Value != AllOption)?.Value
            ?? OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
        EditCurrentLocation = string.Empty;
        EditProductCategory = string.Empty;
        EditManufacturer = string.Empty;
        EditModelName = string.Empty;
        EditMachineNumber = string.Empty;
        EditPurchaseVendor = string.Empty;
        EditPurchasePrice = 0m;
        EditSalePrice = 0m;
        EditCustomerName = string.Empty;
        EditInstallLocation = string.Empty;
        EditDepositText = string.Empty;
        EditMonthlyFee = 0m;
        EditContractMonths = 0;
        EditFreeSupplyItems = string.Empty;
        EditPaidSupplyItems = string.Empty;
        EditAssignedUsername = CanManageAll ? string.Empty : (_session.User?.Username ?? string.Empty);
        EditAssetStatus = "임대진행중";
        EditNotes = string.Empty;
        EditPurchaseDate = null;
        EditDisposalDate = null;
        EditContractDate = null;
        EditInstallDate = null;
        EditContractStartDate = null;
        EditRentalEndDate = null;
        SelectedRow = null;
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    [RelayCommand]
    private async Task OpenReturnReportAsync()
    {
        if (!TryBuildDocumentAsset(out var asset))
            return;

        var company = await _local.GetCompanyProfileAsync(_session);
        var document = _documents.BuildReturnReportDocument(asset, company);
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

        var document = _documents.BuildEquipmentDetailDocument(relatedAssets, customer, company);
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
        var officeOptions = OfficeOptions
            .Where(option => !string.Equals(option.Value, AllOption, StringComparison.OrdinalIgnoreCase))
            .Select(option => new DisplayOption
            {
                Value = option.Value,
                DisplayName = option.DisplayName
            })
            .ToList();
        var companyProfiles = await _local.GetCompanyProfilesAsync();

        var contractModel = _documents.CreateContractDocumentModel(asset, customer, company);
        var editorViewModel = new RentalContractEditorViewModel(
            contractModel,
            _documents,
            officeOptions,
            companyProfiles,
            string.IsNullOrWhiteSpace(asset.ResponsibleOfficeCode) ? company.OfficeCode : asset.ResponsibleOfficeCode,
            _session.HasAdministrativePrivileges);
        var editorWindow = new RentalContractEditorWindow(editorViewModel)
        {
            Owner = GetActiveWindow()
        };
        editorWindow.ShowDialog();
        StatusMessage = "렌탈계약서 작성창을 열었습니다.";
    }

    partial void OnSelectedRowChanged(RentalAssetViewRow? value)
    {
        if (value is null)
        {
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanDeleteSelected));
            return;
        }

        var source = value.Source;
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
        EditCurrentLocation = source.CurrentLocation;
        EditProductCategory = source.ProductCategory;
        EditManufacturer = source.Manufacturer;
        EditModelName = source.ModelName;
        EditMachineNumber = source.MachineNumber;
        EditPurchaseVendor = source.PurchaseVendor;
        EditPurchasePrice = source.PurchasePrice;
        EditSalePrice = source.SalePrice;
        EditCustomerName = source.CustomerName;
        EditInstallLocation = source.InstallLocation;
        EditDepositText = source.DepositText;
        EditMonthlyFee = source.MonthlyFee;
        EditContractMonths = source.ContractMonths;
        EditFreeSupplyItems = source.FreeSupplyItems;
        EditPaidSupplyItems = source.PaidSupplyItems;
        EditAssignedUsername = string.IsNullOrWhiteSpace(source.AssignedUsername)
            ? (_session.User?.Username ?? string.Empty)
            : source.AssignedUsername;
        EditAssetStatus = source.AssetStatus;
        EditNotes = source.Notes;
        EditPurchaseDate = ToDateTime(source.PurchaseDate);
        EditDisposalDate = ToDateTime(source.DisposalDate);
        EditContractDate = ToDateTime(source.ContractDate);
        EditInstallDate = ToDateTime(source.InstallDate);
        EditContractStartDate = ToDateTime(source.ContractStartDate);
        EditRentalEndDate = ToDateTime(source.RentalEndDate);
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanDeleteSelected));
    }

    private bool TryBuildDocumentAsset(out LocalRentalAsset asset)
    {
        if (string.IsNullOrWhiteSpace(EditCustomerName))
        {
            StatusMessage = "거래처명을 입력하거나 렌탈 자산을 선택하세요.";
            asset = new LocalRentalAsset();
            return false;
        }

        if (string.IsNullOrWhiteSpace(EditModelName))
        {
            StatusMessage = "모델명을 입력하세요.";
            asset = new LocalRentalAsset();
            return false;
        }

        asset = new LocalRentalAsset
        {
            Id = EditId,
            CustomerId = EditCustomerId ?? SelectedRow?.Source.CustomerId,
            ItemId = EditItemId ?? SelectedRow?.Source.ItemId,
            BillingProfileId = SelectedRow?.Source.BillingProfileId,
            ManagementId = EditManagementId,
            ManagementNumber = EditManagementNumber,
            ManagementCompanyCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(EditOfficeCode, _session.OfficeCode),
            CurrentLocation = EditCurrentLocation,
            ProductCategory = EditProductCategory,
            Manufacturer = EditManufacturer,
            ModelName = EditModelName,
            MachineNumber = EditMachineNumber,
            PurchaseVendor = EditPurchaseVendor,
            PurchaseDate = ToDateOnly(EditPurchaseDate),
            DisposalDate = ToDateOnly(EditDisposalDate),
            PurchasePrice = EditPurchasePrice,
            SalePrice = EditSalePrice,
            CustomerName = EditCustomerName,
            InstallLocation = EditInstallLocation,
            DepositText = EditDepositText,
            MonthlyFee = EditMonthlyFee,
            ContractMonths = EditContractMonths,
            ContractDate = ToDateOnly(EditContractDate),
            InstallDate = ToDateOnly(EditInstallDate),
            ContractStartDate = ToDateOnly(EditContractStartDate),
            RentalEndDate = ToDateOnly(EditRentalEndDate),
            FreeSupplyItems = EditFreeSupplyItems,
            PaidSupplyItems = EditPaidSupplyItems,
            ResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(EditOfficeCode, _session.OfficeCode),
            AssignedUsername = EditAssignedUsername,
            AssetStatus = EditAssetStatus,
            Notes = EditNotes
        };

        return true;
    }

    private async Task<LocalCustomer?> ResolveDocumentCustomerAsync(LocalRentalAsset asset)
    {
        if (asset.CustomerId is Guid customerId && customerId != Guid.Empty)
        {
            var byId = await _local.GetCustomerAsync(customerId, _session);
            if (byId is not null)
                return byId;
        }

        if (string.IsNullOrWhiteSpace(asset.CustomerName))
            return null;

        return (await _local.GetCustomersAsync(_session))
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
        var customers = await _local.GetCustomersAsync(_session);
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
        var items = await _local.GetItemsAsync();
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
                    $"재고 {item.CurrentStock:N0}"
                }.Where(value => !string.IsNullOrWhiteSpace(value))),
                Tag = item
            })
            .ToList();
    }

    public async Task<IReadOnlyList<LookupRow>> BuildPurchaseVendorLookupRowsAsync()
    {
        var customers = await _local.GetCustomersAsync(_session);
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
        EditModelName = item.NameOriginal?.Trim() ?? string.Empty;
        EditProductCategory = item.CategoryName?.Trim() ?? string.Empty;
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
        OfficeOptions.Clear();
        OfficeOptions.Add(new DisplayOption { Value = AllOption, DisplayName = AllOption });
        foreach (var office in await _local.GetOfficesAsync())
        {
            OfficeOptions.Add(new DisplayOption
            {
                Value = office.Code,
                DisplayName = office.Name
            });
        }

        SelectedOfficeFilter ??= OfficeOptions.FirstOrDefault();
        if (!OfficeOptions.Any(option => option.Value == EditOfficeCode) && OfficeOptions.Count > 1)
            EditOfficeCode = OfficeOptions[1].Value;

        AssignedUsernameOptions.Clear();
        AssignedUsernameOptions.Add(AllOption);
        foreach (var username in await _rental.GetAssignedUsernamesAsync())
            AssignedUsernameOptions.Add(username);
        if (!AssignedUsernameOptions.Contains(SelectedAssignedUsernameFilter))
            SelectedAssignedUsernameFilter = AllOption;
    }

    private void SelectRow(Guid entityId)
    {
        SelectedRow = Rows.FirstOrDefault(row => row.Source.Id == entityId);
    }

    private bool CanEditScope(string? assignedUsername, string? officeCode)
    {
        if (CanManageAll)
            return true;

        var username = (_session.User?.Username ?? string.Empty).Trim();
        var normalizedAssigned = (assignedUsername ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedAssigned) &&
            string.Equals(normalizedAssigned, username, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rowOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet);
        var userOffice = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
        return string.IsNullOrWhiteSpace(normalizedAssigned) &&
               !string.IsNullOrWhiteSpace(rowOffice) &&
               string.Equals(rowOffice, userOffice, StringComparison.OrdinalIgnoreCase);
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
            ? Coalesce(asset.ManagementNumber, asset.ManagementId, asset.ModelName)
            : asset.CustomerName;
        return string.Concat((candidate ?? string.Empty).Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)));
    }

    private static string Coalesce(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "문서";
}
