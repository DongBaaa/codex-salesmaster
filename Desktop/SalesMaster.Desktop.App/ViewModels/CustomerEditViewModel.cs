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
    [ObservableProperty] private DateOnly _registerDate = DateOnly.FromDateTime(DateTime.Today);

    [ObservableProperty] private string _notes = string.Empty;

    [ObservableProperty] private bool _isNew = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public ObservableCollection<LocalCustomerCategory> Categories { get; } = new();
    public string[] PriceGrades { get; } = ["매출단가", "A_단가 적용", "B_단가 적용", "C_단가 적용", "소매단가"];

    public CustomerEditViewModel(LocalStateService local)
    {
        _local = local;
    }

    public async Task LoadAsync(LocalCustomer? customer = null)
    {
        var cats = await _local.GetCategoriesAsync();
        Categories.Clear();
        foreach (var c in cats)
            Categories.Add(c);

        if (customer is null)
        {
            IsNew = true;
            CustomerId = Guid.NewGuid();
            Name = MobilePhone = FaxNumber = Phone = Representative = string.Empty;
            Department = ContactPerson = BusinessNumber = BusinessType = BusinessItem = string.Empty;
            Address = DetailAddress = Recipient = Email = HomePage = Notes = string.Empty;
            PriceGrade = "매출단가";
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
        PriceGrade = customer.PriceGrade;
        Notes = customer.Notes;
        CategoryId = customer.CategoryId;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!await ValidateBeforeSaveAsync())
            return;

        await DoSaveAsync();
        SavedAndClose?.Invoke();
    }

    [RelayCommand]
    private async Task SaveAndNewAsync()
    {
        if (!await ValidateBeforeSaveAsync())
            return;

        await DoSaveAsync();
        await LoadAsync();
        SavedAndNew?.Invoke();
    }

    private async Task<bool> ValidateBeforeSaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "거래처명을 입력하세요.";
            return false;
        }

        var normalizedName = Name.Trim();
        var customers = await _local.GetCustomersAsync();

        var duplicatedName = customers.Any(c =>
            c.Id != CustomerId &&
            string.Equals(c.NameOriginal.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase));

        if (duplicatedName)
        {
            StatusMessage = "동일한 거래처명이 이미 존재합니다. 거래처명을 확인하세요.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(BusinessNumber))
        {
            var normalizedBizNumber = BusinessNumber.Trim();
            var duplicatedBizNumber = customers.Any(c =>
                c.Id != CustomerId &&
                !string.IsNullOrWhiteSpace(c.BusinessNumber) &&
                string.Equals(c.BusinessNumber.Trim(), normalizedBizNumber, StringComparison.OrdinalIgnoreCase));

            if (duplicatedBizNumber)
            {
                StatusMessage = "동일한 사업자번호가 이미 존재합니다.";
                return false;
            }
        }

        return true;
    }

    private async Task DoSaveAsync()
    {
        var normalizedName = Name.Trim();

        var c = new LocalCustomer
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
            Notes = Notes,
        };

        await _local.UpsertCustomerAsync(c);
        StatusMessage = "저장되었습니다.";
    }
}
