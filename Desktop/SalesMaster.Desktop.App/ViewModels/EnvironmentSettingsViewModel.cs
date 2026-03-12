using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class EnvironmentSettingsViewModel : ObservableObject
{
    private const string LegacySourceDbPathSettingKey = "LegacyMigration.SourceDbPath";
    private const string LegacyCustomerExcelPathSettingKey = "LegacyMigration.CustomerExcelPath";
    private const string LegacyItemExcelPathSettingKey = "LegacyMigration.ItemExcelPath";

    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly ErpApiClient _api;
    private readonly LegacyDataMigrationService _legacyMigrationService;

    private Guid _companyProfileId = Guid.NewGuid();

    [ObservableProperty] private string _statusMessage = "환경설정을 불러왔습니다.";
    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _companyTradeName = string.Empty;
    [ObservableProperty] private string _companyRepresentative = string.Empty;
    [ObservableProperty] private string _companyBusinessNumber = string.Empty;
    [ObservableProperty] private string _companyBusinessType = string.Empty;
    [ObservableProperty] private string _companyBusinessItem = string.Empty;
    [ObservableProperty] private string _companyAddress = string.Empty;
    [ObservableProperty] private string _companyContactNumber = string.Empty;
    [ObservableProperty] private string _companyEmail = string.Empty;
    [ObservableProperty] private string _companyBankAccountText = string.Empty;
    [ObservableProperty] private byte[]? _companyStampImage;
    [ObservableProperty] private string _companyStampImagePath = "(없음)";

    [ObservableProperty] private string _legacySourceDbPath = string.Empty;
    [ObservableProperty] private string _legacyCustomerExcelPath = string.Empty;
    [ObservableProperty] private string _legacyItemExcelPath = string.Empty;
    [ObservableProperty] private string _legacyMigrationStatus = "원본 데이터 추출/가져오기 대기";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditOfficeCode))]
    private LocalOffice? _selectedOffice;
    [ObservableProperty] private string _officeCode = string.Empty;
    [ObservableProperty] private string _officeName = string.Empty;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditOfficeCode))]
    private bool _isNewOffice = true;

    [ObservableProperty] private UserAccountDto? _selectedUser;
    [ObservableProperty] private Guid _editingUserId;
    [ObservableProperty] private string _editingUsername = string.Empty;
    [ObservableProperty] private string _editingUserRole = "User";
    [ObservableProperty] private bool _editingUserIsActive = true;
    [ObservableProperty] private string _editingPassword = string.Empty;
    [ObservableProperty] private string _editingPasswordConfirm = string.Empty;

    public ObservableCollection<LocalOffice> Offices { get; } = new();
    public ObservableCollection<UserAccountDto> Users { get; } = new();

    public bool CanManageUsers => _session.IsAdmin && !_session.IsOfflineMode;
    public bool CanEditOfficeCode =>
        IsNewOffice ||
        (_session.IsAdmin && SelectedOffice is not null);
    public string UserManagementHint => CanManageUsers
        ? "사용자 ID, 담당지점, 권한, 비밀번호를 관리합니다."
        : _session.IsOfflineMode
            ? "오프라인 모드에서는 사용자 관리를 사용할 수 없습니다."
            : "관리자 계정으로 로그인해야 사용자 관리를 사용할 수 있습니다.";

    public EnvironmentSettingsViewModel(LocalStateService local, SessionState session, ErpApiClient api)
    {
        _local = local;
        _session = session;
        _api = api;
        _legacyMigrationService = new LegacyDataMigrationService(local);
    }

    public async Task InitializeAsync()
    {
        IsBusy = true;
        try
        {
            await LoadCompanyProfileAsync();
            await LoadLegacyMigrationSettingsAsync();
            await ReloadOfficesAsync();
            await ReloadMasterOptionsAsync();
            await ReloadUsersAsync();
            NewOffice();
            NewUser();
            StatusMessage = "환경설정을 불러왔습니다.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task SaveCompanyProfileAsync()
    {
        if (!_session.HasPermission("CompanyProfile.Edit") && !_session.IsAdmin)
        {
            StatusMessage = "회사 정보를 수정할 권한이 없습니다.";
            return;
        }

        var profile = new LocalCompanyProfile
        {
            Id = _companyProfileId,
            TradeName = CompanyTradeName,
            Representative = CompanyRepresentative,
            BusinessNumber = CompanyBusinessNumber,
            BusinessType = CompanyBusinessType,
            BusinessItem = CompanyBusinessItem,
            Address = CompanyAddress,
            ContactNumber = CompanyContactNumber,
            Email = CompanyEmail,
            BankAccountText = CompanyBankAccountText,
            StampImage = CompanyStampImage
        };

        await _local.SaveCompanyProfileAsync(profile);
        StatusMessage = "회사 정보를 저장했습니다.";
    }

    [RelayCommand]
    private void SelectStampImage()
    {
        var dialog = new OpenFileDialog
        {
            Title = "직인 이미지 선택",
            Filter = "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp"
        };

        if (dialog.ShowDialog() != true)
            return;

        CompanyStampImage = File.ReadAllBytes(dialog.FileName);
        CompanyStampImagePath = Path.GetFileName(dialog.FileName);
        StatusMessage = "직인 이미지를 선택했습니다.";
    }

    [RelayCommand]
    private void ClearStampImage()
    {
        CompanyStampImage = null;
        CompanyStampImagePath = "(없음)";
        StatusMessage = "직인 이미지를 삭제했습니다.";
    }

    [RelayCommand]
    private async Task SelectLegacySourceDbPathAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "외부 레거시 DB(FDB) 선택",
            Filter = "Firebird DB|*.fdb|모든 파일|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() != true)
            return;

        LegacySourceDbPath = dialog.FileName;
        await PersistLegacyMigrationSettingsAsync();
    }

    [RelayCommand]
    private async Task SelectLegacyCustomerExcelPathAsync()
    {
        var initialDirectory = Path.GetDirectoryName(LegacyCustomerExcelPath);
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
            initialDirectory = AppContext.BaseDirectory;

        var dialog = new SaveFileDialog
        {
            Title = "거래처 추출 엑셀 경로 선택",
            Filter = "Excel 파일|*.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            FileName = "거래처 목록.xlsx",
            InitialDirectory = initialDirectory
        };

        if (dialog.ShowDialog() != true)
            return;

        LegacyCustomerExcelPath = dialog.FileName;
        await PersistLegacyMigrationSettingsAsync();
    }

    [RelayCommand]
    private async Task SelectLegacyItemExcelPathAsync()
    {
        var initialDirectory = Path.GetDirectoryName(LegacyItemExcelPath);
        if (string.IsNullOrWhiteSpace(initialDirectory) || !Directory.Exists(initialDirectory))
            initialDirectory = AppContext.BaseDirectory;

        var dialog = new SaveFileDialog
        {
            Title = "제품 추출 엑셀 경로 선택",
            Filter = "Excel 파일|*.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            FileName = "제품 목록.xlsx",
            InitialDirectory = initialDirectory
        };

        if (dialog.ShowDialog() != true)
            return;

        LegacyItemExcelPath = dialog.FileName;
        await PersistLegacyMigrationSettingsAsync();
    }

    [RelayCommand]
    private async Task ExportLegacyDataAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LegacySourceDbPath) || !File.Exists(LegacySourceDbPath))
            {
                StatusMessage = "외부 레거시 DB 경로를 먼저 확인하세요.";
                return;
            }

            if (string.IsNullOrWhiteSpace(LegacyCustomerExcelPath) || string.IsNullOrWhiteSpace(LegacyItemExcelPath))
            {
                StatusMessage = "거래처/제품 엑셀 경로를 먼저 지정하세요.";
                return;
            }

            IsBusy = true;
            LegacyMigrationStatus = "외부 레거시 데이터를 엑셀로 추출 중...";
            var result = await _legacyMigrationService.ExportFromOriginalAsync(
                LegacySourceDbPath,
                LegacyCustomerExcelPath,
                LegacyItemExcelPath);

            await PersistLegacyMigrationSettingsAsync();
            LegacyMigrationStatus = $"추출 완료: 거래처 {result.CustomerCount:N0}건, 제품 {result.ItemCount:N0}건";
            StatusMessage = LegacyMigrationStatus;
        }
        catch (Exception ex)
        {
            LegacyMigrationStatus = $"추출 실패: {ex.Message}";
            StatusMessage = LegacyMigrationStatus;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ImportLegacyExcelDataAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(LegacyCustomerExcelPath) || !File.Exists(LegacyCustomerExcelPath))
            {
                StatusMessage = "거래처 엑셀 파일 경로를 확인하세요.";
                return;
            }

            if (string.IsNullOrWhiteSpace(LegacyItemExcelPath) || !File.Exists(LegacyItemExcelPath))
            {
                StatusMessage = "제품 엑셀 파일 경로를 확인하세요.";
                return;
            }

            IsBusy = true;
            LegacyMigrationStatus = "엑셀 데이터를 코덱스 레거시 판매관리로 가져오는 중...";
            var result = await _legacyMigrationService.ImportFromExcelAsync(
                LegacyCustomerExcelPath,
                LegacyItemExcelPath);

            await PersistLegacyMigrationSettingsAsync();
            LegacyMigrationStatus =
                $"가져오기 완료: 거래처 +{result.CreatedCustomers:N0}/수정 {result.UpdatedCustomers:N0}, 제품 +{result.CreatedItems:N0}/수정 {result.UpdatedItems:N0}";
            StatusMessage = LegacyMigrationStatus;
        }
        catch (Exception ex)
        {
            LegacyMigrationStatus = $"가져오기 실패: {ex.Message}";
            StatusMessage = LegacyMigrationStatus;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportAndImportLegacyDataAsync()
    {
        await ExportLegacyDataAsync();
        if (!LegacyMigrationStatus.StartsWith("추출 완료", StringComparison.Ordinal))
            return;

        await ImportLegacyExcelDataAsync();
    }

    [RelayCommand]
    private void NewOffice()
    {
        SelectedOffice = null;
        OfficeCode = string.Empty;
        OfficeName = string.Empty;
        IsNewOffice = true;
        StatusMessage = "새 담당지점을 입력한 뒤 저장하세요.";
    }

    [RelayCommand]
    private async Task SaveOfficeAsync()
    {
        var previousOfficeCode = SelectedOffice?.Code;
        var office = new LocalOffice
        {
            Id = IsNewOffice ? Guid.NewGuid() : SelectedOffice?.Id ?? Guid.NewGuid(),
            Code = OfficeCode,
            Name = OfficeName,
            IsHeadOffice = !IsNewOffice && SelectedOffice?.IsHeadOffice == true
        };

        var result = await _local.SaveOfficeAsync(office);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        if (!string.IsNullOrWhiteSpace(previousOfficeCode) &&
            string.Equals(_session.OfficeCode, previousOfficeCode, StringComparison.OrdinalIgnoreCase))
        {
            _session.SetOfficeCode(office.Code);
        }

        await ReloadOfficesAsync();
        SelectedOffice = Offices.FirstOrDefault(current => current.Id == result.EntityId);
    }

    [RelayCommand]
    private async Task DeleteOfficeAsync()
    {
        if (SelectedOffice is null)
        {
            StatusMessage = "삭제할 담당지점을 선택하세요.";
            return;
        }

        var result = await _local.DeleteOfficeAsync(SelectedOffice.Id);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await ReloadOfficesAsync();
        NewOffice();
    }

    [RelayCommand]
    private async Task ReloadOfficesAsync()
    {
        var offices = await _local.GetOfficesAsync();
        Offices.Clear();
        foreach (var office in offices)
            Offices.Add(office);

        RefreshUserOfficeOptions();
    }

    [RelayCommand]
    private void NewUser()
    {
        SelectedUser = null;
        EditingUserId = Guid.Empty;
        EditingUsername = string.Empty;
        EditingUserRole = "User";
        EditingUserIsActive = true;
        EditingPassword = string.Empty;
        EditingPasswordConfirm = string.Empty;
        SetDefaultEditingUserOfficeCode();
        StatusMessage = "새 사용자를 추가할 수 있습니다.";
    }

    [RelayCommand]
    private async Task SaveUserAsync()
    {
        if (!CanManageUsers)
        {
            StatusMessage = _session.IsOfflineMode
                ? "오프라인 모드에서는 사용자 관리를 사용할 수 없습니다."
                : "사용자 관리 권한이 없습니다.";
            return;
        }

        if (string.IsNullOrWhiteSpace(EditingUsername))
        {
            StatusMessage = "아이디를 입력하세요.";
            return;
        }

        if (EditingUserId == Guid.Empty && string.IsNullOrWhiteSpace(EditingPassword))
        {
            StatusMessage = "신규 사용자는 비밀번호를 입력해야 합니다.";
            return;
        }

        if (!string.IsNullOrWhiteSpace(EditingPassword) &&
            !string.Equals(EditingPassword, EditingPasswordConfirm, StringComparison.Ordinal))
        {
            StatusMessage = "비밀번호 확인이 일치하지 않습니다.";
            return;
        }

        if (string.IsNullOrWhiteSpace(EditingUserOfficeCode))
        {
            StatusMessage = "사용자의 담당지점을 선택하세요.";
            return;
        }

        var permissions = BuildPermissionsForRole(EditingUserRole);
        try
        {
            IsBusy = true;
            if (EditingUserId == Guid.Empty)
            {
                await _api.CreateUserAsync(new CreateUserRequest
                {
                    Username = EditingUsername.Trim(),
                    Password = EditingPassword,
                    Role = EditingUserRole,
                    OfficeCode = EditingUserOfficeCode,
                    IsActive = EditingUserIsActive,
                    Permissions = permissions
                });
                StatusMessage = "사용자를 추가했습니다.";
            }
            else
            {
                await _api.UpdateUserAsync(EditingUserId, new UpdateUserRequest
                {
                    Username = EditingUsername.Trim(),
                    Role = EditingUserRole,
                    OfficeCode = EditingUserOfficeCode,
                    IsActive = EditingUserIsActive,
                    Permissions = permissions
                });

                if (!string.IsNullOrWhiteSpace(EditingPassword))
                {
                    await _api.UpdateUserPasswordAsync(EditingUserId, new UpdateUserPasswordRequest
                    {
                        Password = EditingPassword
                    });
                }

                StatusMessage = "사용자 정보를 저장했습니다.";
            }

            await ReloadUsersAsync();
            EditingPassword = string.Empty;
            EditingPasswordConfirm = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = $"사용자 저장 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DeleteUserAsync()
    {
        if (!CanManageUsers)
        {
            StatusMessage = "사용자 관리 권한이 없습니다.";
            return;
        }

        if (SelectedUser is null)
        {
            StatusMessage = "삭제할 사용자를 선택하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            await _api.DeleteUserAsync(SelectedUser.Id);
            StatusMessage = "사용자를 삭제했습니다.";
            await ReloadUsersAsync();
            NewUser();
        }
        catch (Exception ex)
        {
            StatusMessage = $"사용자 삭제 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReloadUsersAsync()
    {
        Users.Clear();
        if (!CanManageUsers)
            return;

        var users = await _api.GetUsersAsync();
        foreach (var user in users.OrderBy(current => current.Username, StringComparer.OrdinalIgnoreCase))
            Users.Add(user);
    }

    partial void OnSelectedOfficeChanged(LocalOffice? value)
    {
        if (value is null)
        {
            if (!IsNewOffice)
                NewOffice();
            return;
        }

        IsNewOffice = false;
        OfficeCode = value.Code;
        OfficeName = value.Name;
    }

    partial void OnSelectedUserChanged(UserAccountDto? value)
    {
        if (value is null)
            return;

        EditingUserId = value.Id;
        EditingUsername = value.Username;
        EditingUserRole = string.Equals(value.Role, "Admin", StringComparison.OrdinalIgnoreCase) ? "Admin" : "User";
        EditingUserOfficeCode = value.OfficeCode;
        EditingUserIsActive = value.IsActive;
        EditingPassword = string.Empty;
        EditingPasswordConfirm = string.Empty;
    }

    private async Task LoadCompanyProfileAsync()
    {
        var profile = await _local.GetCompanyProfileAsync();
        if (profile is null)
            return;

        _companyProfileId = profile.Id;
        CompanyTradeName = profile.TradeName;
        CompanyRepresentative = profile.Representative;
        CompanyBusinessNumber = profile.BusinessNumber;
        CompanyBusinessType = profile.BusinessType;
        CompanyBusinessItem = profile.BusinessItem;
        CompanyAddress = profile.Address;
        CompanyContactNumber = profile.ContactNumber;
        CompanyEmail = profile.Email;
        CompanyBankAccountText = profile.BankAccountText;
        CompanyStampImage = profile.StampImage;
        CompanyStampImagePath = profile.StampImage is { Length: > 0 } ? "(이미지 있음)" : "(없음)";
    }

    private async Task LoadLegacyMigrationSettingsAsync()
    {
        var defaultDb = GetDefaultLegacySourceDbPath();
        var defaultCustomerExcel = Path.Combine(AppContext.BaseDirectory, "거래처 목록.xlsx");
        var defaultItemExcel = Path.Combine(AppContext.BaseDirectory, "제품 목록.xlsx");

        LegacySourceDbPath = await _local.GetSettingAsync(LegacySourceDbPathSettingKey) ?? defaultDb;
        LegacyCustomerExcelPath = await _local.GetSettingAsync(LegacyCustomerExcelPathSettingKey) ?? defaultCustomerExcel;
        LegacyItemExcelPath = await _local.GetSettingAsync(LegacyItemExcelPathSettingKey) ?? defaultItemExcel;

        if (string.IsNullOrWhiteSpace(LegacySourceDbPath))
            LegacySourceDbPath = defaultDb;
        if (string.IsNullOrWhiteSpace(LegacyCustomerExcelPath))
            LegacyCustomerExcelPath = defaultCustomerExcel;
        if (string.IsNullOrWhiteSpace(LegacyItemExcelPath))
            LegacyItemExcelPath = defaultItemExcel;
    }

    private async Task PersistLegacyMigrationSettingsAsync()
    {
        await _local.SetSettingAsync(LegacySourceDbPathSettingKey, LegacySourceDbPath ?? string.Empty);
        await _local.SetSettingAsync(LegacyCustomerExcelPathSettingKey, LegacyCustomerExcelPath ?? string.Empty);
        await _local.SetSettingAsync(LegacyItemExcelPathSettingKey, LegacyItemExcelPath ?? string.Empty);
    }

    private static string GetDefaultLegacySourceDbPath()
    {
        var candidate = @"C:\LegacyVendor\LegacySalesApp\DATA\SALE_ACE_DATA.FDB";
        return File.Exists(candidate) ? candidate : string.Empty;
    }

    private static List<string> BuildPermissionsForRole(string? role)
    {
        if (!string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase))
            return new List<string>();

        return new List<string>
        {
            "CompanyProfile.Edit",
            "Amount.ViewSales",
            "Amount.ViewPurchase",
            "Settings.Edit",
            "Data.BackupRestore"
        };
    }
}
