using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class CustomerEditViewModel : ObservableObject
{
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
    [ObservableProperty] private string _responsibleOfficeCode = DomainConstants.OfficeUznet;
    [ObservableProperty] private DateOnly _registerDate = DateOnly.FromDateTime(DateTime.Today);

    [ObservableProperty] private string _notes = string.Empty;

    [ObservableProperty] private bool _isNew = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public ObservableCollection<LocalCustomerCategory> Categories { get; } = new();
    public ObservableCollection<string> OfficeCodes { get; } = new();
    public string[] PriceGrades { get; } = ["매출단가", "A_단가 적용", "B_단가 적용", "C_단가 적용", "소매단가"];

    public CustomerEditViewModel(LocalStateService local, SessionState session)
    {
        _local = local;
        _session = session;
    }

    public async Task LoadAsync(LocalCustomer? customer = null)
    {
        await _local.EnsureCustomerCategoryIntegrityAsync();
        var categories = await _local.GetCategoriesAsync();
        Categories.Clear();
        foreach (var category in categories)
            Categories.Add(category);

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
            OfficeCodes.Add(DomainConstants.OfficeUznet);
            OfficeCodes.Add(DomainConstants.OfficeYeonsu);
        }

        if (customer is null)
        {
            var defaultOfficeCode = string.IsNullOrWhiteSpace(_session.OfficeCode)
                ? DomainConstants.OfficeUznet
                : _session.OfficeCode.Trim().ToUpperInvariant();

            IsNew = true;
            CustomerId = Guid.NewGuid();
            Name = MobilePhone = FaxNumber = Phone = Representative = string.Empty;
            Department = ContactPerson = BusinessNumber = BusinessType = BusinessItem = string.Empty;
            Address = DetailAddress = Recipient = Email = HomePage = Notes = string.Empty;
            PriceGrade = "매출단가";
            ResponsibleOfficeCode = defaultOfficeCode;
            RegisterDate = DateOnly.FromDateTime(DateTime.Today);
            CategoryId = null;
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
        PriceGrade = string.IsNullOrWhiteSpace(customer.PriceGrade) ? "매출단가" : customer.PriceGrade;
        ResponsibleOfficeCode = string.IsNullOrWhiteSpace(customer.ResponsibleOfficeCode)
            ? DomainConstants.OfficeUznet
            : customer.ResponsibleOfficeCode.Trim().ToUpperInvariant();
        if (!OfficeCodes.Contains(ResponsibleOfficeCode))
            OfficeCodes.Add(ResponsibleOfficeCode);
        Notes = customer.Notes;
        CategoryId = customer.CategoryId;
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
            ResponsibleOfficeCode = string.IsNullOrWhiteSpace(ResponsibleOfficeCode)
                ? DomainConstants.OfficeUznet
                : ResponsibleOfficeCode.Trim().ToUpperInvariant(),
            Notes = Notes,
        };

        if (_session.IsAdmin)
        {
            await _local.UpsertCustomerAsync(customer);
            StatusMessage = "거래처를 저장했습니다.";
            return true;
        }

        var result = await _local.UpsertCustomerAsync(customer, _session);
        StatusMessage = result.Message;
        return result.Success;
    }
}
