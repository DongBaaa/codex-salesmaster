using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using 거래플랜.Desktop.App;
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
    private readonly ErpApiClient? _api;
    private string _baselineStateSignature = string.Empty;
    private long _loadedCustomerRevision;
    private Guid? _editingContractId;

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
    public bool IsEditingSelectedContract => _editingContractId.HasValue && SelectedContract?.Id == _editingContractId.Value;
    public string ContractEntryModeText => IsEditingSelectedContract ? "선택 계약서 편집" : "새 계약서 입력";
    public bool HasPendingChanges => HasPendingCustomerChanges || HasPendingContractDraft || HasPendingSelectedContractChanges;
    public bool HasMeaningfulDraftContentForClose => HasMeaningfulDraftContent() || HasPendingContractDraft || HasPendingSelectedContractChanges;

    public ObservableCollection<LocalCustomerCategory> Categories { get; } = new();
    public ObservableCollection<string> OfficeCodes { get; } = new();
    public ObservableCollection<string> TradeTypes { get; } = new();
    public ObservableCollection<string> PriceGrades { get; } = new();
    public ObservableCollection<string> ContractTypes { get; } = new();
    public ObservableCollection<LocalCustomerContract> Contracts { get; } = new();

    public CustomerEditViewModel(LocalStateService local, SessionState session, ErpApiClient? api = null)
    {
        _local = local;
        _session = session;
        _api = api ?? App.TryGetService<ErpApiClient>();

        foreach (var contractType in DefaultContractTypes)
            ContractTypes.Add(contractType);

        CaptureBaselineState();
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
        var writableOfficeCodes = _local.GetWritableOfficeCodesForSession(_session);
        var writableOfficeCodeSet = writableOfficeCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        OfficeCodes.Clear();
        foreach (var officeCode in offices
                     .Select(office => office.Code)
                     .Where(code => writableOfficeCodeSet.Contains(code))
                     .Where(code => !string.IsNullOrWhiteSpace(code))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(code => code, StringComparer.OrdinalIgnoreCase))
        {
            OfficeCodes.Add(officeCode);
        }

        if (OfficeCodes.Count == 0)
        {
            foreach (var officeCode in writableOfficeCodes)
                OfficeCodes.Add(officeCode);
        }

        if (OfficeCodes.Count == 0)
        {
            OfficeCodes.Add(NormalizeOfficeCode(_session.OfficeCode));
        }

        StatusMessage = string.Empty;

        if (customer is null)
        {
            var defaultOfficeCode = NormalizeOfficeCode(_session.OfficeCode);

            IsNew = true;
            _loadedCustomerRevision = 0;
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
            _editingContractId = null;
            ResetContractEntry(makePrimary: true);
            NotifyContractEntryModeChanged();
            CaptureBaselineState();
            return;
        }

        IsNew = false;
        _loadedCustomerRevision = customer.Revision;
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
        if (!PriceGrades.Contains(PriceGrade))
            PriceGrades.Add(PriceGrade);
        if (!OfficeCodes.Contains(ResponsibleOfficeCode))
            OfficeCodes.Add(ResponsibleOfficeCode);
        Notes = customer.Notes;
        CategoryId = customer.CategoryId;
        if ((!CategoryId.HasValue || CategoryId == Guid.Empty) &&
            CustomerClassificationNormalizer.TryExtractCompositeCategoryAndTradeType(customer.TradeType, out var inferredCategory, out _))
        {
            CategoryId = inferredCategory.Id;
        }

        await LoadContractsAsync(CustomerId);
        CaptureBaselineState();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var shouldPersistCustomer = IsNew || HasPendingCustomerChanges;

        if (shouldPersistCustomer)
        {
            if (!ValidateBeforeSave())
                return;

            if (!await DoSaveAsync(showConflictDialog: true))
                return;
        }

        if (!await PersistPendingContractDraftAsync(
                shouldPersistCustomer
                    ? "거래처와 계약서 초안을 저장했습니다. PDF는 나중에 추가할 수 있습니다."
                    : "계약서 초안을 저장했습니다. PDF는 나중에 추가할 수 있습니다.",
                shouldPersistCustomer
                    ? "거래처와 선택 계약서 정보를 저장했습니다."
                    : "선택 계약서 정보를 저장했습니다."))
            return;

        SavedAndClose?.Invoke();
    }

    [RelayCommand]
    private async Task SaveAndNewAsync()
    {
        var shouldPersistCustomer = IsNew || HasPendingCustomerChanges;

        if (shouldPersistCustomer)
        {
            if (!ValidateBeforeSave())
                return;

            if (!await DoSaveAsync(showConflictDialog: true))
                return;
        }

        if (!await PersistPendingContractDraftAsync(
                shouldPersistCustomer
                    ? "거래처와 계약서 초안을 저장했습니다. 새 입력으로 넘어갑니다."
                    : "계약서 초안을 저장했습니다. 새 입력으로 넘어갑니다.",
                shouldPersistCustomer
                    ? "거래처와 선택 계약서 정보를 저장하고 새 입력으로 넘어갑니다."
                    : "선택 계약서 정보를 저장하고 새 입력으로 넘어갑니다."))
            return;

        await LoadAsync();
        SavedAndNew?.Invoke();
    }

    [RelayCommand]
    private void BeginNewContractEntry()
    {
        _editingContractId = null;
        SelectedContract = null;
        ResetContractEntry(makePrimary: Contracts.Count == 0);
        NotifyContractEntryModeChanged();
        StatusMessage = "새 계약서 입력으로 전환했습니다.";
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

        var editingContract = IsEditingSelectedContract ? SelectedContract : null;
        if (editingContract is not null && ContractHasPdfFile(editingContract))
        {
            StatusMessage = "이미 PDF가 등록된 계약서입니다. 새 계약서를 등록하려면 '새 계약서' 버튼으로 입력을 초기화하세요.";
            MessageBox.Show(
                "이미 PDF가 등록된 계약서입니다. 새 계약서를 등록하려면 '새 계약서' 버튼으로 입력을 초기화한 뒤 진행하세요.",
                "계약서 PDF 등록",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var contractType = SelectedContractType;
        var signedDate = ContractSignedDate;
        var expireDate = ContractExpireDate;
        var description = ContractDescription;
        var isPrimary = NewContractIsPrimary || (Contracts.Count == 0 && editingContract is null);

        var result = editingContract is null
            ? await _local.SaveCustomerContractAsync(
                CustomerId,
                dialog.FileName,
                contractType,
                signedDate,
                expireDate,
                description,
                isPrimary,
                _session)
            : await _local.AttachCustomerContractPdfAsync(
                editingContract.Id,
                dialog.FileName,
                contractType,
                signedDate,
                expireDate,
                description,
                isPrimary,
                _session,
                editingContract.Revision);

        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await LoadContractsAsync(CustomerId, result.EntityId);
    }

    [RelayCommand]
    private async Task OpenContractAsync()
    {
        var contract = SelectedContract;
        if (contract is null)
        {
            StatusMessage = "열 계약서를 선택하세요.";
            return;
        }

        if (!ContractHasPdfFile(contract))
        {
            StatusMessage = "선택한 계약서에는 아직 PDF 파일이 등록되지 않았습니다.";
            MessageBox.Show(
                "선택한 계약서에는 아직 PDF 파일이 등록되지 않았습니다. 'PDF 추가' 버튼으로 파일을 연결하세요.",
                "계약서 열기",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            if (!CustomerContractContentService.HasLocalContent(contract))
                StatusMessage = "계약서 PDF를 서버에서 내려받는 중입니다.";

            var readyContract = await CustomerContractContentService.EnsureContentAsync(contract, _local, _session, _api);
            CustomerContractPreviewService.Open(readyContract);
            StatusMessage = "계약서 PDF를 열었습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"계약서를 열지 못했습니다. {ex.Message}";
        }
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
        var result = await _local.SetPrimaryCustomerContractAsync(selectedId, _session, SelectedContract.Revision);
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
        var result = await _local.DeleteCustomerContractAsync(deletedId, _session, SelectedContract.Revision);
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

        if (!CustomerTradeTypes.TryNormalize(TradeType, out var normalizedTradeType))
        {
            StatusMessage = "거래구분은 매출, 매입, 매출/매입 중 하나만 선택할 수 있습니다.";
            return false;
        }

        TradeType = normalizedTradeType;

        return true;
    }

    private async Task<bool> DoSaveAsync()
        => await DoSaveAsync(waitForServerWrite: true, successMessage: "거래처를 저장했습니다.", showConflictDialog: true);

    private async Task<bool> DoSaveAsync(bool showConflictDialog)
        => await DoSaveAsync(waitForServerWrite: true, successMessage: "거래처를 저장했습니다.", showConflictDialog: showConflictDialog);

    public async Task<bool> TryAutoSaveOnCloseAsync()
    {
        if (!HasPendingChanges || !HasMeaningfulDraftContentForClose)
            return true;

        var shouldPersistCustomer = IsNew || HasPendingCustomerChanges;
        if (shouldPersistCustomer)
        {
            if (!ValidateBeforeSave())
                return false;

            if (!await DoSaveAsync(waitForServerWrite: false, successMessage: "거래처를 자동 저장했습니다.", showConflictDialog: false))
                return false;
        }

        if (!await PersistPendingContractDraftAsync(
                shouldPersistCustomer
                    ? "거래처와 계약서 초안을 자동 저장했습니다."
                    : "계약서 초안을 자동 저장했습니다.",
                shouldPersistCustomer
                    ? "거래처와 선택 계약서 정보를 자동 저장했습니다."
                    : "선택 계약서 정보를 자동 저장했습니다."))
            return false;

        return true;
    }

    private async Task<bool> DoSaveAsync(bool waitForServerWrite, string successMessage, bool showConflictDialog)
    {
        var normalizedName = Name.Trim();

        var customer = new LocalCustomer
        {
            Id = CustomerId,
            Revision = _loadedCustomerRevision,
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

        var result = await _local.UpsertCustomerAsync(customer, _session);
        StatusMessage = result.Message;
        if (!result.Success)
        {
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

        if (result.Success)
        {
            _loadedCustomerRevision = customer.Revision;
            if (waitForServerWrite)
            {
                var serverWriteResult = await _local.WaitForServerWriteWithTimeoutAsync(TimeSpan.FromSeconds(3));
                StatusMessage = LocalStateService.ComposeServerWriteStatusMessage(successMessage, serverWriteResult);
            }
            else
            {
                StatusMessage = successMessage;
            }

            IsNew = false;
            CaptureBaselineState();
        }

        return true;
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
    }

    private async Task<bool> PersistPendingContractDraftAsync(string draftSuccessMessage, string updateSuccessMessage)
    {
        if (HasPendingSelectedContractChanges && SelectedContract is not null)
        {
            var updateResult = await _local.UpdateCustomerContractAsync(
                SelectedContract.Id,
                SelectedContractType,
                ContractSignedDate,
                ContractExpireDate,
                ContractDescription,
                NewContractIsPrimary,
                _session,
                SelectedContract.Revision);

            StatusMessage = updateResult.Message;
            if (!updateResult.Success)
                return false;

            await LoadContractsAsync(CustomerId, updateResult.EntityId);
            StatusMessage = updateSuccessMessage;
            return true;
        }

        if (!HasPendingContractDraft)
            return true;

        var result = await _local.SaveCustomerContractDraftAsync(
            CustomerId,
            SelectedContractType,
            ContractSignedDate,
            ContractExpireDate,
            ContractDescription,
            NewContractIsPrimary || Contracts.Count == 0,
            _session);

        StatusMessage = result.Message;
        if (!result.Success)
            return false;

        await LoadContractsAsync(CustomerId, result.EntityId);
        StatusMessage = draftSuccessMessage;
        return true;
    }

    private void ResetContractEntry(bool makePrimary)
    {
        SelectedContractType = ContractTypes.FirstOrDefault() ?? DefaultContractTypes[0];
        ContractSignedDate = null;
        ContractExpireDate = null;
        ContractDescription = string.Empty;
        NewContractIsPrimary = makePrimary;
    }

    partial void OnSelectedContractChanged(LocalCustomerContract? value)
    {
        if (value is null)
        {
            _editingContractId = null;
            ResetContractEntry(makePrimary: Contracts.Count == 0);
            NotifyContractEntryModeChanged();
            return;
        }

        if (!ContractTypes.Contains(value.ContractType))
            ContractTypes.Add(value.ContractType);

        _editingContractId = value.Id;
        SelectedContractType = string.IsNullOrWhiteSpace(value.ContractType)
            ? ContractTypes.FirstOrDefault() ?? DefaultContractTypes[0]
            : value.ContractType;
        ContractSignedDate = value.SignedDate;
        ContractExpireDate = value.ExpireDate;
        ContractDescription = value.Description ?? string.Empty;
        NewContractIsPrimary = value.IsPrimary;
        NotifyContractEntryModeChanged();
    }

    private void NotifyContractEntryModeChanged()
    {
        OnPropertyChanged(nameof(IsEditingSelectedContract));
        OnPropertyChanged(nameof(ContractEntryModeText));
    }

    private static string NormalizeOfficeCode(string? officeCode)
    => OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode, DomainConstants.OfficeUsenet);

    private static bool ContractHasPdfFile(LocalCustomerContract? contract)
        => CustomerContractContentService.HasRegisteredFile(contract);

    private bool HasPendingCustomerChanges
        => !string.Equals(_baselineStateSignature, BuildStateSignature(), StringComparison.Ordinal);

    private bool HasPendingContractDraft
    {
        get
        {
            if (IsEditingSelectedContract)
                return false;

            var defaultContractType = ContractTypes.FirstOrDefault() ?? DefaultContractTypes[0];
            var expectedPrimary = Contracts.Count == 0;
            return ContractSignedDate.HasValue
                   || ContractExpireDate.HasValue
                   || !string.IsNullOrWhiteSpace(ContractDescription)
                   || !string.Equals(SelectedContractType, defaultContractType, StringComparison.Ordinal)
                   || NewContractIsPrimary != expectedPrimary;
        }
    }

    private bool HasPendingSelectedContractChanges
    {
        get
        {
            if (!IsEditingSelectedContract || SelectedContract is null)
                return false;

            return !string.Equals(
                       (SelectedContractType ?? string.Empty).Trim(),
                       (SelectedContract.ContractType ?? string.Empty).Trim(),
                       StringComparison.Ordinal)
                   || ContractSignedDate != SelectedContract.SignedDate
                   || ContractExpireDate != SelectedContract.ExpireDate
                   || !string.Equals(
                       (ContractDescription ?? string.Empty).Trim(),
                       (SelectedContract.Description ?? string.Empty).Trim(),
                       StringComparison.Ordinal)
                   || NewContractIsPrimary != SelectedContract.IsPrimary;
        }
    }

    private bool HasMeaningfulDraftContent()
        => !string.IsNullOrWhiteSpace(Name)
           || !string.IsNullOrWhiteSpace(Phone)
           || !string.IsNullOrWhiteSpace(MobilePhone)
           || !string.IsNullOrWhiteSpace(FaxNumber)
           || !string.IsNullOrWhiteSpace(Representative)
           || !string.IsNullOrWhiteSpace(Department)
           || !string.IsNullOrWhiteSpace(ContactPerson)
           || !string.IsNullOrWhiteSpace(BusinessNumber)
           || !string.IsNullOrWhiteSpace(BusinessType)
           || !string.IsNullOrWhiteSpace(BusinessItem)
           || !string.IsNullOrWhiteSpace(Address)
           || !string.IsNullOrWhiteSpace(DetailAddress)
           || !string.IsNullOrWhiteSpace(Recipient)
           || !string.IsNullOrWhiteSpace(Email)
           || !string.IsNullOrWhiteSpace(HomePage)
           || !string.IsNullOrWhiteSpace(Notes)
           || CategoryId.HasValue
           || !string.Equals(PriceGrade, "매출단가", StringComparison.Ordinal)
           || !string.Equals(NormalizeOfficeCode(ResponsibleOfficeCode), DomainConstants.OfficeUsenet, StringComparison.OrdinalIgnoreCase)
           || !string.Equals(CustomerTradeTypes.Normalize(TradeType), CustomerTradeTypes.Sales, StringComparison.Ordinal)
           || Contracts.Count > 0;

    private void CaptureBaselineState()
        => _baselineStateSignature = BuildStateSignature();

    private string BuildStateSignature()
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(CustomerId.ToString("D"))
            .Append('|').Append(Name ?? string.Empty)
            .Append('|').Append(CategoryId?.ToString("D") ?? string.Empty)
            .Append('|').Append(Phone ?? string.Empty)
            .Append('|').Append(MobilePhone ?? string.Empty)
            .Append('|').Append(FaxNumber ?? string.Empty)
            .Append('|').Append(Representative ?? string.Empty)
            .Append('|').Append(Department ?? string.Empty)
            .Append('|').Append(ContactPerson ?? string.Empty)
            .Append('|').Append(BusinessNumber ?? string.Empty)
            .Append('|').Append(BusinessType ?? string.Empty)
            .Append('|').Append(BusinessItem ?? string.Empty)
            .Append('|').Append(Address ?? string.Empty)
            .Append('|').Append(DetailAddress ?? string.Empty)
            .Append('|').Append(Recipient ?? string.Empty)
            .Append('|').Append(Email ?? string.Empty)
            .Append('|').Append(HomePage ?? string.Empty)
            .Append('|').Append(PriceGrade ?? string.Empty)
            .Append('|').Append(NormalizeOfficeCode(ResponsibleOfficeCode))
            .Append('|').Append(CustomerTradeTypes.Normalize(TradeType))
            .Append('|').Append(Notes ?? string.Empty)
            .Append('|').Append(IsNew);

        return builder.ToString();
    }
}
