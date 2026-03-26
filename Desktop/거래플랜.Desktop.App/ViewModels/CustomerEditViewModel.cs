using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class CustomerEditViewModel : ObservableObject
{
    private static readonly string[] DefaultContractTypes =
    [
        "거래계약서",
        "렌탈계약서",
        "유지보수계약서",
        "특약서",
        "기타"
    ];

    private readonly LocalStateService _local;
    private readonly SessionState _session;

    public event Action? SavedAndNew;
    public event Action? SavedAndClose;

    [ObservableProperty] private Guid _customerId = Guid.NewGuid();
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private Guid? _categoryId;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _mobilePhone = string.Empty;
    [ObservableProperty] private string _faxNumber = string.Empty;
    [ObservableProperty] private string _representative = string.Empty;
    [ObservableProperty] private string _department = string.Empty;
    [ObservableProperty] private string _contactPerson = string.Empty;

    [ObservableProperty] private string _businessNumber = string.Empty;
    [ObservableProperty] private string _businessType = string.Empty;
    [ObservableProperty] private string _businessItem = string.Empty;

    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private string _detailAddress = string.Empty;

    [ObservableProperty] private string _recipient = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _homePage = string.Empty;

    [ObservableProperty] private string _priceGrade = "매출단가";
    [ObservableProperty] private string _responsibleOfficeCode = DomainConstants.OfficeUsenet;
    [ObservableProperty] private string _tradeType = CustomerTradeTypes.Sales;
    [ObservableProperty] private DateOnly _registerDate = DateOnly.FromDateTime(DateTime.Today);

    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private LocalCustomerContract? _selectedContract;
    [ObservableProperty] private string _selectedContractType = DefaultContractTypes[0];
    [ObservableProperty] private DateOnly? _contractSignedDate;
    [ObservableProperty] private DateOnly? _contractExpireDate;
    [ObservableProperty] private string _contractDescription = string.Empty;
    [ObservableProperty] private bool _newContractIsPrimary;

    [ObservableProperty] private bool _isNew = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public ObservableCollection<LocalCustomerCategory> Categories { get; } = new();
    public ObservableCollection<string> OfficeCodes { get; } = new();
    public ObservableCollection<string> TradeTypes { get; } = new();
    public ObservableCollection<string> PriceGrades { get; } = new();
    public ObservableCollection<string> ContractTypes { get; } = new();
    public ObservableCollection<LocalCustomerContract> Contracts { get; } = new();

    public CustomerEditViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;

        foreach (var contractType in DefaultContractTypes)
            ContractTypes.Add(contractType);
    }

    public async Task LoadAsync(LocalCustomer? customer = null)
    {
        await _local.EnsureCustomerCategoryIntegrityAsync();
        var categories = await _local.GetCategoriesAsync();
        Categories.Clear();
        foreach (var category in categories)
            Categories.Add(category);

        var priceGrades = await _local.GetPriceGradeOptionsAsync();
        PriceGrades.Clear();
        foreach (var priceGrade in priceGrades.Select(option => option.Name))
            PriceGrades.Add(priceGrade);
        if (PriceGrades.Count == 0)
            PriceGrades.Add("매출단가");

        var tradeTypes = await _local.GetTradeTypeOptionsAsync();
        TradeTypes.Clear();
        foreach (var tradeType in tradeTypes.Select(option => option.Name))
            TradeTypes.Add(tradeType);
        if (TradeTypes.Count == 0)
            TradeTypes.Add(CustomerTradeTypes.Sales);

        var offices = await _local.GetOfficesAsync();
        OfficeCodes.Clear();
        foreach (var officeCode in offices
                     .Select(office => office.Code)
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            OfficeCodes.Add(officeCode);
        }

        if (OfficeCodes.Count == 0)
        {
            OfficeCodes.Add(DomainConstants.OfficeUsenet);
            OfficeCodes.Add(DomainConstants.OfficeItworld);
            OfficeCodes.Add(DomainConstants.OfficeYeonsu);
        }

        StatusMessage = string.Empty;

        if (customer is null)
        {
            var defaultOfficeCode = NormalizeOfficeCode(_session.OfficeCode);

            IsNew = true;
            CustomerId = Guid.NewGuid();
            Name = MobilePhone = FaxNumber = Phone = Representative = string.Empty;
            Department = ContactPerson = BusinessNumber = BusinessType = BusinessItem = string.Empty;
            Address = DetailAddress = Recipient = Email = HomePage = Notes = string.Empty;
            PriceGrade = PriceGrades.FirstOrDefault() ?? "매출단가";
            ResponsibleOfficeCode = defaultOfficeCode;
            TradeType = TradeTypes.FirstOrDefault() ?? CustomerTradeTypes.Sales;
            RegisterDate = DateOnly.FromDateTime(DateTime.Today);
            CategoryId = null;
            SelectedContract = null;
            Contracts.Clear();
            ResetContractEntry(makePrimary: true);
            return;
        }

        IsNew = false;
        CustomerId = customer.Id;
        Name = customer.NameOriginal;
        Phone = customer.Phone;
        MobilePhone = customer.MobilePhone;
        FaxNumber = customer.FaxNumber;
        Representative = customer.Representative;
        Department = customer.Department;
        ContactPerson = customer.ContactPerson;
        BusinessNumber = customer.BusinessNumber;
        BusinessType = customer.BusinessType;
        BusinessItem = customer.BusinessItem;
        Address = customer.Address;
        DetailAddress = customer.DetailAddress;
        Recipient = customer.Recipient;
        Email = customer.Email;
        HomePage = customer.HomePage;
        PriceGrade = string.IsNullOrWhiteSpace(customer.PriceGrade)
            ? PriceGrades.FirstOrDefault() ?? "매출단가"
            : customer.PriceGrade;
        ResponsibleOfficeCode = string.IsNullOrWhiteSpace(customer.ResponsibleOfficeCode)
            ? DomainConstants.OfficeUsenet
            : NormalizeOfficeCode(customer.ResponsibleOfficeCode);
        TradeType = CustomerTradeTypes.Normalize(customer.TradeType);
        if (!TradeTypes.Contains(TradeType))
            TradeTypes.Add(TradeType);
        if (!PriceGrades.Contains(PriceGrade))
            PriceGrades.Add(PriceGrade);
        if (!OfficeCodes.Contains(ResponsibleOfficeCode))
            OfficeCodes.Add(ResponsibleOfficeCode);
        Notes = customer.Notes;
        CategoryId = customer.CategoryId;

        await LoadContractsAsync(CustomerId);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!ValidateBeforeSave())
            return;

        if (!await DoSaveAsync())
            return;

        SavedAndClose?.Invoke();
    }

    [RelayCommand]
    private async Task SaveAndNewAsync()
    {
        if (!ValidateBeforeSave())
            return;

        if (!await DoSaveAsync())
            return;

        await LoadAsync();
        SavedAndNew?.Invoke();
    }

    [RelayCommand]
    private async Task AddContractAsync()
    {
        if (IsNew)
        {
            if (!ValidateBeforeSave())
                return;

            if (!await DoSaveAsync())
                return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "거래처 계약서 PDF 선택",
            Filter = "PDF 파일|*.pdf",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
            return;

        var result = await _local.SaveCustomerContractAsync(
            CustomerId,
            dialog.FileName,
            SelectedContractType,
            ContractSignedDate,
            ContractExpireDate,
            ContractDescription,
            NewContractIsPrimary || Contracts.Count == 0,
            _session);

        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await LoadContractsAsync(CustomerId, result.EntityId);
        ResetContractEntry(makePrimary: Contracts.Count == 0);
    }

    [RelayCommand]
    private void OpenContract()
    {
        var contract = SelectedContract;
        if (contract is null)
        {
            StatusMessage = "열 계약서를 선택하세요.";
            return;
        }

        if (contract.FileContent is null || contract.FileContent.Length == 0)
        {
            StatusMessage = "계약서 PDF 내용이 없습니다. 동기화 상태를 확인해주세요.";
            return;
        }

        var previewPath = BuildContractPreviewPath(contract);
        Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);
        File.WriteAllBytes(previewPath, contract.FileContent);

        Process.Start(new ProcessStartInfo(previewPath)
        {
            UseShellExecute = true
        });

        StatusMessage = "계약서 PDF를 열었습니다.";
    }

    [RelayCommand]
    private async Task SetPrimaryContractAsync()
    {
        if (SelectedContract is null)
        {
            StatusMessage = "대표로 지정할 계약서를 선택하세요.";
            return;
        }

        var selectedId = SelectedContract.Id;
        var result = await _local.SetPrimaryCustomerContractAsync(selectedId, _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await LoadContractsAsync(CustomerId, selectedId);
    }

    [RelayCommand]
    private async Task DeleteContractAsync()
    {
        if (SelectedContract is null)
        {
            StatusMessage = "삭제할 계약서를 선택하세요.";
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"선택한 계약서 '{SelectedContract.FileName}'을(를) 삭제하시겠습니까?",
            "계약서 삭제",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);

        if (confirm != System.Windows.MessageBoxResult.Yes)
            return;

        var deletedId = SelectedContract.Id;
        var result = await _local.DeleteCustomerContractAsync(deletedId, _session);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await LoadContractsAsync(CustomerId);
        if (SelectedContract?.Id == deletedId)
            SelectedContract = null;
    }

    private bool ValidateBeforeSave()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "거래처명을 입력하세요.";
            return false;
        }

        return true;
    }

    private async Task<bool> DoSaveAsync()
    {
        var normalizedName = Name.Trim();

        var customer = new LocalCustomer
        {
            Id = CustomerId,
            NameOriginal = normalizedName,
            NameMatchKey = normalizedName.ToUpperInvariant(),
            CategoryId = CategoryId,
            Phone = Phone,
            MobilePhone = MobilePhone,
            FaxNumber = FaxNumber,
            Representative = Representative,
            Department = Department,
            ContactPerson = ContactPerson,
            BusinessNumber = BusinessNumber,
            BusinessType = BusinessType,
            BusinessItem = BusinessItem,
            Address = Address,
            DetailAddress = DetailAddress,
            Recipient = Recipient,
            Email = Email,
            HomePage = HomePage,
            PriceGrade = PriceGrade,
            TradeType = CustomerTradeTypes.Normalize(TradeType),
            ResponsibleOfficeCode = NormalizeOfficeCode(ResponsibleOfficeCode),
            Notes = Notes,
        };

        if (_session.HasAdministrativePrivileges)
        {
            await _local.UpsertCustomerAsync(customer);
            var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
            StatusMessage = LocalStateService.ComposeServerWriteStatusMessage("거래처를 저장했습니다.", serverWriteResult);
            IsNew = false;
            return true;
        }

        var result = await _local.UpsertCustomerAsync(customer, _session);
        StatusMessage = result.Message;
        if (result.Success)
        {
            var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
            StatusMessage = LocalStateService.ComposeServerWriteStatusMessage(result.Message, serverWriteResult);
            IsNew = false;
        }
        return result.Success;
    }

    private async Task LoadContractsAsync(Guid customerId, Guid? selectContractId = null)
    {
        Contracts.Clear();
        var contracts = await _local.GetCustomerContractsAsync(customerId, _session);
        foreach (var contract in contracts)
            Contracts.Add(contract);

        SelectedContract = selectContractId.HasValue
            ? Contracts.FirstOrDefault(current => current.Id == selectContractId.Value)
            : Contracts.FirstOrDefault();

        ResetContractEntry(makePrimary: Contracts.Count == 0);
    }

    private void ResetContractEntry(bool makePrimary)
    {
        SelectedContractType = ContractTypes.FirstOrDefault() ?? DefaultContractTypes[0];
        ContractSignedDate = null;
        ContractExpireDate = null;
        ContractDescription = string.Empty;
        NewContractIsPrimary = makePrimary;
    }

    private static string BuildContractPreviewPath(LocalCustomerContract contract)
    {
        var previewDir = Path.Combine(AppPaths.CustomerContractPreviewDir, contract.CustomerId.ToString("N"));
        var extension = string.IsNullOrWhiteSpace(Path.GetExtension(contract.FileName))
            ? ".pdf"
            : Path.GetExtension(contract.FileName);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(contract.FileName);
        if (string.IsNullOrWhiteSpace(fileNameWithoutExtension))
            fileNameWithoutExtension = "customer-contract";

        var safeBaseName = new string(fileNameWithoutExtension.Where(ch => !Path.GetInvalidFileNameChars().Contains(ch)).ToArray());
        if (string.IsNullOrWhiteSpace(safeBaseName))
            safeBaseName = "customer-contract";

        return Path.Combine(previewDir, $"{safeBaseName}_{contract.Id:N}{extension}");
    }

    private static string NormalizeOfficeCode(string? officeCode)
    => OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode, DomainConstants.OfficeUsenet);
}
