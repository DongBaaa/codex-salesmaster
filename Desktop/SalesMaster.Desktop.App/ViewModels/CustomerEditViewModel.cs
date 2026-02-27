using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class CustomerEditViewModel : ObservableObject
{
    private readonly LocalStateService _local;

    public event Action? SavedAndNew;   // 연속입력 F6
    public event Action? SavedAndClose; // 저장 후 닫기

    // ── 거래처 기본 정보 ──────────────────────────────────────────────────
    [ObservableProperty] private Guid _customerId = Guid.NewGuid();
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private Guid? _categoryId;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _mobilePhone = string.Empty;
    [ObservableProperty] private string _faxNumber = string.Empty;
    [ObservableProperty] private string _representative = string.Empty;
    [ObservableProperty] private string _department = string.Empty;
    [ObservableProperty] private string _contactPerson = string.Empty;

    // ── 사업자 정보 ───────────────────────────────────────────────────────
    [ObservableProperty] private string _businessNumber = string.Empty;
    [ObservableProperty] private string _businessType = string.Empty;
    [ObservableProperty] private string _businessItem = string.Empty;

    // ── 주소 ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _address = string.Empty;
    [ObservableProperty] private string _detailAddress = string.Empty;

    // ── 온라인 정보 ───────────────────────────────────────────────────────
    [ObservableProperty] private string _recipient = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _homePage = string.Empty;

    // ── 거래 조건 ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _priceGrade = "매출단가";
    [ObservableProperty] private DateOnly _registerDate = DateOnly.FromDateTime(DateTime.Today);

    // ── 메모 ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _notes = string.Empty;

    // ── 상태 ─────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isNew = true;
    [ObservableProperty][NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    // ── 목록 ─────────────────────────────────────────────────────────────
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
        foreach (var c in cats) Categories.Add(c);

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
        }
        else
        {
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
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "거래처명을 입력하세요.";
            return;
        }
        await DoSaveAsync();
        SavedAndClose?.Invoke();
    }

    // 연속입력 (F6): 저장 후 새 폼
    [RelayCommand]
    private async Task SaveAndNewAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "거래처명을 입력하세요.";
            return;
        }
        await DoSaveAsync();
        await LoadAsync();          // 새 폼으로 초기화
        SavedAndNew?.Invoke();
    }

    private async Task DoSaveAsync()
    {
        var c = new LocalCustomer
        {
            Id = CustomerId,
            NameOriginal = Name,
            NameMatchKey = Name.ToUpperInvariant(),
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
