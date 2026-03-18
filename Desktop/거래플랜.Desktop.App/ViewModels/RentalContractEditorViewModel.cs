using System.Collections.ObjectModel;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalContractEditorViewModel : ObservableObject
{
    private readonly RentalDocumentService _documents;
    private readonly Dictionary<string, LocalCompanyProfile> _companyProfilesByOfficeCode = new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressManagementOfficeChange;

    public event Action? RequestClose;

    [ObservableProperty] private bool _isEditMode;
    [ObservableProperty] private bool _canEditManagementOffice;
    [ObservableProperty] private string _selectedManagementOfficeCode = string.Empty;
    [ObservableProperty] private string _toggleEditButtonText = "계약서 수정";
    [ObservableProperty] private string _statusMessage = "렌탈계약서를 불러왔습니다.";
    [ObservableProperty] private FixedDocument? _previewDocument;

    [ObservableProperty] private string _title = "사무기기 렌탈 계약서";
    [ObservableProperty] private string _tenantName = string.Empty;
    [ObservableProperty] private string _tenantBusinessNumber = string.Empty;
    [ObservableProperty] private string _tenantAddress = string.Empty;
    [ObservableProperty] private string _tenantRepresentative = string.Empty;
    [ObservableProperty] private string _companyName = string.Empty;
    [ObservableProperty] private string _companyBusinessNumber = string.Empty;
    [ObservableProperty] private string _companyAddress = string.Empty;
    [ObservableProperty] private string _companyRepresentative = string.Empty;
    [ObservableProperty] private string _companyContactNumber = string.Empty;
    [ObservableProperty] private string _companyFaxNumber = string.Empty;
    [ObservableProperty] private string _managementNumber = string.Empty;
    [ObservableProperty] private string _modelName = string.Empty;
    [ObservableProperty] private string _machineNumber = string.Empty;
    [ObservableProperty] private string _depositText = string.Empty;
    [ObservableProperty] private decimal _monthlyFee;
    [ObservableProperty] private string _installLocation = string.Empty;
    [ObservableProperty] private DateTime _contractDate = DateTime.Today;
    [ObservableProperty] private DateTime _contractStartDate = DateTime.Today;
    [ObservableProperty] private DateTime? _contractEndDate;
    [ObservableProperty] private string _introText = string.Empty;
    [ObservableProperty] private string _closingLine1 = string.Empty;
    [ObservableProperty] private string _closingLine2 = string.Empty;

    private byte[]? _companyStampImage;

    public ObservableCollection<DisplayOption> ManagementOfficeOptions { get; } = new();
    public ObservableCollection<RentalContractClauseEditItem> Clauses { get; } = new();
    public ObservableCollection<RentalContractTextEditItem> SpecialTerms { get; } = new();

    public bool CanSelectManagementOffice => CanEditManagementOffice && IsEditMode;

    public RentalContractEditorViewModel(
        RentalContractDocumentModel model,
        RentalDocumentService documents,
        IEnumerable<DisplayOption>? managementOfficeOptions = null,
        IEnumerable<LocalCompanyProfile>? companyProfiles = null,
        string? initialManagementOfficeCode = null,
        bool canEditManagementOffice = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(documents);

        _documents = documents;
        CanEditManagementOffice = canEditManagementOffice;

        if (managementOfficeOptions is not null)
        {
            foreach (var option in managementOfficeOptions.Where(option => !string.IsNullOrWhiteSpace(option.Value)))
                ManagementOfficeOptions.Add(option);
        }

        if (companyProfiles is not null)
        {
            foreach (var profile in companyProfiles
                         .Where(profile => !string.IsNullOrWhiteSpace(profile.OfficeCode))
                         .GroupBy(profile => NormalizeOfficeCode(profile.OfficeCode))
                         .Select(group => group.OrderByDescending(profile => profile.IsDefaultForOffice).ThenBy(profile => profile.ProfileName).First()))
            {
                _companyProfilesByOfficeCode[NormalizeOfficeCode(profile.OfficeCode)] = profile;
            }
        }

        ApplyModel(model);
        InitializeManagementOfficeSelection(initialManagementOfficeCode);
        RefreshPreview();
    }

    partial void OnIsEditModeChanged(bool value)
    {
        ToggleEditButtonText = value ? "수정 완료" : "계약서 수정";
        OnPropertyChanged(nameof(CanSelectManagementOffice));
        if (!value)
            RefreshPreview();
    }

    partial void OnCanEditManagementOfficeChanged(bool value)
        => OnPropertyChanged(nameof(CanSelectManagementOffice));

    partial void OnSelectedManagementOfficeCodeChanged(string value)
    {
        if (_suppressManagementOfficeChange)
            return;

        ApplyManagementOfficeDefaults(value);
    }

    [RelayCommand]
    private void ToggleEditMode()
        => IsEditMode = !IsEditMode;

    [RelayCommand]
    private void RefreshPreview()
    {
        PreviewDocument = _documents.BuildContractDocument(BuildModel());
        StatusMessage = "계약서 미리보기를 갱신했습니다.";
    }

    [RelayCommand]
    private void Close()
        => RequestClose?.Invoke();

    public RentalContractDocumentModel BuildModel()
    {
        var model = new RentalContractDocumentModel
        {
            Title = Title?.Trim() ?? string.Empty,
            TenantName = TenantName?.Trim() ?? string.Empty,
            TenantBusinessNumber = TenantBusinessNumber?.Trim() ?? string.Empty,
            TenantAddress = TenantAddress?.Trim() ?? string.Empty,
            TenantRepresentative = TenantRepresentative?.Trim() ?? string.Empty,
            CompanyName = CompanyName?.Trim() ?? string.Empty,
            CompanyBusinessNumber = CompanyBusinessNumber?.Trim() ?? string.Empty,
            CompanyAddress = CompanyAddress?.Trim() ?? string.Empty,
            CompanyRepresentative = CompanyRepresentative?.Trim() ?? string.Empty,
            CompanyContactNumber = CompanyContactNumber?.Trim() ?? string.Empty,
            CompanyFaxNumber = CompanyFaxNumber?.Trim() ?? string.Empty,
            ManagementNumber = ManagementNumber?.Trim() ?? string.Empty,
            ModelName = ModelName?.Trim() ?? string.Empty,
            MachineNumber = MachineNumber?.Trim() ?? string.Empty,
            DepositText = DepositText?.Trim() ?? string.Empty,
            MonthlyFee = MonthlyFee,
            InstallLocation = InstallLocation?.Trim() ?? string.Empty,
            ContractDate = DateOnly.FromDateTime(ContractDate == default ? DateTime.Today : ContractDate),
            ContractStartDate = DateOnly.FromDateTime(ContractStartDate == default ? DateTime.Today : ContractStartDate),
            ContractEndDate = ContractEndDate.HasValue ? DateOnly.FromDateTime(ContractEndDate.Value) : null,
            IntroText = IntroText?.Trim() ?? string.Empty,
            ClosingLine1 = ClosingLine1?.Trim() ?? string.Empty,
            ClosingLine2 = ClosingLine2?.Trim() ?? string.Empty,
            CompanyStampImage = _companyStampImage
        };

        foreach (var clause in Clauses)
        {
            model.Clauses.Add(new RentalContractClauseModel
            {
                Number = clause.Number?.Trim() ?? string.Empty,
                Title = clause.Title?.Trim() ?? string.Empty,
                Body = clause.Body?.Trim() ?? string.Empty
            });
        }

        foreach (var term in SpecialTerms.Where(term => !string.IsNullOrWhiteSpace(term.Text)))
            model.SpecialTerms.Add(term.Text.Trim());

        return model;
    }

    private void ApplyModel(RentalContractDocumentModel model)
    {
        Title = model.Title;
        TenantName = model.TenantName;
        TenantBusinessNumber = model.TenantBusinessNumber;
        TenantAddress = model.TenantAddress;
        TenantRepresentative = model.TenantRepresentative;
        CompanyName = model.CompanyName;
        CompanyBusinessNumber = model.CompanyBusinessNumber;
        CompanyAddress = model.CompanyAddress;
        CompanyRepresentative = model.CompanyRepresentative;
        CompanyContactNumber = model.CompanyContactNumber;
        CompanyFaxNumber = model.CompanyFaxNumber;
        ManagementNumber = model.ManagementNumber;
        ModelName = model.ModelName;
        MachineNumber = model.MachineNumber;
        DepositText = model.DepositText;
        MonthlyFee = model.MonthlyFee;
        InstallLocation = model.InstallLocation;
        ContractDate = model.ContractDate.ToDateTime(TimeOnly.MinValue);
        ContractStartDate = model.ContractStartDate.ToDateTime(TimeOnly.MinValue);
        ContractEndDate = model.ContractEndDate?.ToDateTime(TimeOnly.MinValue);
        IntroText = model.IntroText;
        ClosingLine1 = model.ClosingLine1;
        ClosingLine2 = model.ClosingLine2;
        _companyStampImage = model.CompanyStampImage;

        Clauses.Clear();
        foreach (var clause in model.Clauses)
        {
            Clauses.Add(new RentalContractClauseEditItem
            {
                Number = clause.Number,
                Title = clause.Title,
                Body = clause.Body
            });
        }

        SpecialTerms.Clear();
        foreach (var term in model.SpecialTerms)
            SpecialTerms.Add(new RentalContractTextEditItem { Text = term });
    }

    private void InitializeManagementOfficeSelection(string? initialManagementOfficeCode)
    {
        if (ManagementOfficeOptions.Count == 0)
            return;

        var targetOfficeCode = NormalizeOfficeCode(initialManagementOfficeCode);
        if (string.IsNullOrWhiteSpace(targetOfficeCode) || !ManagementOfficeOptions.Any(option => NormalizeOfficeCode(option.Value) == targetOfficeCode))
        {
            targetOfficeCode = ResolveOfficeCodeFromCompanyName(CompanyName)
                               ?? NormalizeOfficeCode(ManagementOfficeOptions.First().Value);
        }

        _suppressManagementOfficeChange = true;
        SelectedManagementOfficeCode = targetOfficeCode;
        _suppressManagementOfficeChange = false;

        if (ShouldApplyInitialManagementOfficeDefaults())
            ApplyManagementOfficeDefaults(targetOfficeCode, false);
    }

    private bool ShouldApplyInitialManagementOfficeDefaults()
    {
        return string.IsNullOrWhiteSpace(CompanyName) &&
               string.IsNullOrWhiteSpace(CompanyBusinessNumber) &&
               string.IsNullOrWhiteSpace(CompanyAddress) &&
               string.IsNullOrWhiteSpace(CompanyRepresentative) &&
               string.IsNullOrWhiteSpace(CompanyContactNumber);
    }

    private void ApplyManagementOfficeDefaults(string? officeCode, bool updateStatus = true)
    {
        var normalizedOfficeCode = NormalizeOfficeCode(officeCode);
        if (string.IsNullOrWhiteSpace(normalizedOfficeCode))
            return;

        if (!_companyProfilesByOfficeCode.TryGetValue(normalizedOfficeCode, out var profile))
            return;

        CompanyName = profile.TradeName?.Trim() ?? string.Empty;
        CompanyBusinessNumber = profile.BusinessNumber?.Trim() ?? string.Empty;
        CompanyAddress = profile.Address?.Trim() ?? string.Empty;
        CompanyRepresentative = profile.Representative?.Trim() ?? string.Empty;
        CompanyContactNumber = profile.ContactNumber?.Trim() ?? string.Empty;
        CompanyFaxNumber = string.Empty;
        _companyStampImage = profile.StampImage;

        if (updateStatus)
        {
            var officeDisplayName = ManagementOfficeOptions.FirstOrDefault(option => NormalizeOfficeCode(option.Value) == normalizedOfficeCode)?.DisplayName
                                    ?? normalizedOfficeCode;
            StatusMessage = $"관리업체를 {officeDisplayName} 기본값으로 반영했습니다.";
        }
    }

    private string? ResolveOfficeCodeFromCompanyName(string? companyName)
    {
        var normalizedCompanyName = companyName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCompanyName))
            return null;

        var profile = _companyProfilesByOfficeCode.Values.FirstOrDefault(profile =>
            string.Equals(profile.TradeName?.Trim(), normalizedCompanyName, StringComparison.OrdinalIgnoreCase));
        return profile is null ? null : NormalizeOfficeCode(profile.OfficeCode);
    }

    private static string NormalizeOfficeCode(string? officeCode)
        => string.IsNullOrWhiteSpace(officeCode) ? string.Empty : officeCode.Trim().ToUpperInvariant();
}

public sealed partial class RentalContractClauseEditItem : ObservableObject
{
    [ObservableProperty] private string _number = string.Empty;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string _body = string.Empty;
}

public sealed partial class RentalContractTextEditItem : ObservableObject
{
    [ObservableProperty] private string _text = string.Empty;
}
