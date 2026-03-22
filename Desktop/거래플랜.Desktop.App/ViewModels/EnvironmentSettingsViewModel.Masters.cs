using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class EnvironmentSettingsViewModel
{
    [ObservableProperty] private LocalCustomerCategory? _selectedCategoryOption;
    [ObservableProperty] private string _categoryOptionName = string.Empty;
    [ObservableProperty] private bool _categoryOptionIsSystemDefault;
    [ObservableProperty] private bool _isNewCategoryOption = true;

    [ObservableProperty] private LocalPriceGradeOption? _selectedPriceGradeOption;
    [ObservableProperty] private string _priceGradeOptionName = string.Empty;
    [ObservableProperty] private string _priceGradePriceSource = SelectionOptionDefaults.PriceSourceSales;
    [ObservableProperty] private bool _priceGradeOptionIsSystemDefault;
    [ObservableProperty] private bool _isNewPriceGradeOption = true;

    [ObservableProperty] private LocalTradeTypeOption? _selectedTradeTypeOption;
    [ObservableProperty] private string _tradeTypeOptionName = string.Empty;
    [ObservableProperty] private bool _tradeTypeAllowsSales = true;
    [ObservableProperty] private bool _tradeTypeAllowsPurchase;
    [ObservableProperty] private bool _tradeTypeOptionIsSystemDefault;
    [ObservableProperty] private bool _isNewTradeTypeOption = true;

    [ObservableProperty] private LocalItemCategoryOption? _selectedItemCategoryOption;
    [ObservableProperty] private string _itemCategoryOptionName = string.Empty;
    [ObservableProperty] private bool _itemCategoryOptionIsSystemDefault;
    [ObservableProperty] private bool _isNewItemCategoryOption = true;

    [ObservableProperty] private string _editingUserOfficeCode = string.Empty;
    [ObservableProperty] private string _editingUserScopeType = TenantScopeCatalog.ScopeOfficeOnly;

    public ObservableCollection<LocalCustomerCategory> CustomerCategories { get; } = new();
    public ObservableCollection<LocalPriceGradeOption> PriceGradeOptions { get; } = new();
    public ObservableCollection<LocalTradeTypeOption> TradeTypeOptions { get; } = new();
    public ObservableCollection<LocalItemCategoryOption> ItemCategoryOptions { get; } = new();
    public ObservableCollection<DisplayOption> UserTenantOptions { get; } = new();
    public ObservableCollection<DisplayOption> UserOfficeOptions { get; } = new();
    public ObservableCollection<DisplayOption> TenantStructureRows { get; } = new();
    public ObservableCollection<DisplayOption> TenantSharingPolicyRows { get; } = new();

    public IReadOnlyList<DisplayOption> RoleOptions { get; } =
    [
        new() { Value = "Admin", DisplayName = "관리자" },
        new() { Value = "User", DisplayName = "일반" }
    ];

    public IReadOnlyList<DisplayOption> ScopeTypeOptions { get; } =
    [
        new() { Value = TenantScopeCatalog.ScopeOfficeOnly, DisplayName = "지점 전용" },
        new() { Value = TenantScopeCatalog.ScopeTenantAll, DisplayName = "업체 전체" },
        new() { Value = TenantScopeCatalog.ScopeAdmin, DisplayName = "관리자" }
    ];

    public IReadOnlyList<DisplayOption> PriceSourceOptions { get; } =
    [
        new() { Value = SelectionOptionDefaults.PriceSourceSales, DisplayName = "매출단가" },
        new() { Value = SelectionOptionDefaults.PriceSourceA, DisplayName = "A단가" },
        new() { Value = SelectionOptionDefaults.PriceSourceB, DisplayName = "B단가" },
        new() { Value = SelectionOptionDefaults.PriceSourceC, DisplayName = "C단가" },
        new() { Value = SelectionOptionDefaults.PriceSourceRetail, DisplayName = "소매단가" }
    ];

    private async Task ReloadMasterOptionsAsync()
    {
        await ReloadCustomerCategoriesAsync();
        await ReloadPriceGradeOptionsAsync();
        await ReloadTradeTypeOptionsAsync();
        await ReloadItemCategoryOptionsAsync();
        RefreshTenantPolicyRows();
        RefreshUserTenantOptions();
        RefreshUserOfficeOptions();
        NewCategoryOption();
        NewPriceGradeOption();
        NewTradeTypeOption();
        NewItemCategoryOption();
    }

    private void RefreshUserTenantOptions()
    {
        UserTenantOptions.Clear();
        var tenantRows = TenantDefinitions.Count > 0
            ? TenantDefinitions.Select(current => new
            {
                current.TenantCode,
                DisplayName = string.IsNullOrWhiteSpace(current.DisplayName)
                    ? TenantScopeCatalog.GetTenantDisplayName(current.TenantCode)
                    : current.DisplayName.Trim()
            })
            : TenantScopeCatalog.AllTenants.Select(tenantCode => new
            {
                TenantCode = tenantCode,
                DisplayName = TenantScopeCatalog.GetTenantDisplayName(tenantCode)
            });

        foreach (var tenant in tenantRows)
        {
            UserTenantOptions.Add(new DisplayOption
            {
                Value = tenant.TenantCode,
                DisplayName = $"{tenant.DisplayName} ({tenant.TenantCode})"
            });
        }

        if (UserTenantOptions.Count == 0)
        {
            UserTenantOptions.Add(new DisplayOption
            {
                Value = TenantScopeCatalog.UsenetGroup,
                DisplayName = TenantScopeCatalog.GetTenantDisplayName(TenantScopeCatalog.UsenetGroup)
            });
        }

        SetDefaultEditingUserTenantCode();
    }

    private void RefreshUserOfficeOptions()
    {
        UserOfficeOptions.Clear();
        var selectedTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(EditingUserTenantCode, _session.TenantCode);
        var officeRows = TenantOfficeDefinitions.Count > 0
            ? TenantOfficeDefinitions
                .Where(current => string.Equals(current.TenantCode, selectedTenantCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(current => current.IsHeadOffice)
                .ThenBy(current => GetOfficeSortOrder(current.OfficeCode))
                .ThenBy(current => current.OfficeCode, StringComparer.OrdinalIgnoreCase)
                .Select(current => new { Code = current.OfficeCode, Name = current.DisplayName })
            : Offices
                .Where(current => TenantScopeCatalog.TenantContainsOffice(selectedTenantCode, current.Code))
                .OrderBy(current => GetOfficeSortOrder(current.Code))
                .ThenBy(current => current.Code, StringComparer.OrdinalIgnoreCase)
                .Select(current => new { Code = current.Code, Name = current.Name });

        foreach (var office in officeRows)
        {
            UserOfficeOptions.Add(new DisplayOption
            {
                Value = office.Code,
                DisplayName = $"{office.Name} ({office.Code})"
            });
        }

        if (UserOfficeOptions.Count == 0)
        {
            UserOfficeOptions.Add(new DisplayOption
            {
                Value = DomainConstants.OfficeUsenet,
                DisplayName = $"USENET ({DomainConstants.OfficeUsenet})"
            });
        }

        SetDefaultEditingUserOfficeCode();
    }

    private void SetDefaultEditingUserTenantCode()
    {
        var currentTenant = TenantScopeCatalog.NormalizeTenantCodeOrDefault(EditingUserTenantCode, _session.TenantCode);
        if (UserTenantOptions.Any(option => string.Equals(option.Value, currentTenant, StringComparison.OrdinalIgnoreCase)))
        {
            EditingUserTenantCode = currentTenant;
            return;
        }

        EditingUserTenantCode = UserTenantOptions.FirstOrDefault()?.Value
            ?? TenantScopeCatalog.NormalizeTenantCodeOrDefault(_session.TenantCode);
    }

    private void SetDefaultEditingUserOfficeCode()
    {
        var currentCode = NormalizeOfficeCode(EditingUserOfficeCode);
        if (UserOfficeOptions.Any(option => string.Equals(option.Value, currentCode, StringComparison.OrdinalIgnoreCase)))
        {
            EditingUserOfficeCode = currentCode;
            return;
        }

        var defaultOffice = TenantScopeCatalog.GetOfficeCodesForTenant(EditingUserTenantCode).FirstOrDefault()
            ?? _session.OfficeCode;
        EditingUserOfficeCode = UserOfficeOptions.FirstOrDefault()?.Value
            ?? NormalizeOfficeCode(defaultOffice)
            ?? DomainConstants.OfficeUsenet;
    }

    private void RefreshTenantPolicyRows()
    {
        TenantStructureRows.Clear();
        TenantStructureRows.Add(new DisplayOption
        {
            Value = TenantScopeCatalog.UsenetGroup,
            DisplayName = $"USENET_GROUP: {OfficeCodeCatalog.Usenet}, {OfficeCodeCatalog.Yeonsu}"
        });
        TenantStructureRows.Add(new DisplayOption
        {
            Value = TenantScopeCatalog.Itworld,
            DisplayName = $"ITWORLD: {OfficeCodeCatalog.Itworld}"
        });

        TenantSharingPolicyRows.Clear();
        TenantSharingPolicyRows.Add(new DisplayOption
        {
            Value = "YEONSU->USENET",
            DisplayName = "연수구에서 등록/수정한 거래처·거래내역은 유즈넷 상급권한에서 조회/관리 가능"
        });
        TenantSharingPolicyRows.Add(new DisplayOption
        {
            Value = "USENET_SCOPE",
            DisplayName = "연수구 사용자는 연수구 지점 데이터만 조회, 유즈넷은 USENET_GROUP 전체 조회"
        });
        TenantSharingPolicyRows.Add(new DisplayOption
        {
            Value = "ITWORLD_ISOLATED",
            DisplayName = "아이티월드는 별도 업체 권역으로 취급하며 1차 구현에서는 테넌트 분리 기준으로 격리"
        });
    }

    [RelayCommand]
    private void NewCategoryOption()
    {
        SelectedCategoryOption = null;
        CategoryOptionName = string.Empty;
        CategoryOptionIsSystemDefault = false;
        IsNewCategoryOption = true;
    }

    [RelayCommand]
    private async Task SaveCategoryOptionAsync()
    {
        var result = await _local.SaveCustomerCategoryAsync(new LocalCustomerCategory
        {
            Id = IsNewCategoryOption ? Guid.NewGuid() : SelectedCategoryOption?.Id ?? Guid.NewGuid(),
            Name = CategoryOptionName,
            IsSystemDefault = CategoryOptionIsSystemDefault
        });

        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await _local.WaitForServerWriteAsync();
        await ReloadCustomerCategoriesAsync();
        SelectedCategoryOption = CustomerCategories.FirstOrDefault(option => option.Id == result.EntityId);
    }

    [RelayCommand]
    private async Task DeleteCategoryOptionAsync()
    {
        if (SelectedCategoryOption is null)
        {
            StatusMessage = "삭제할 고객분류를 선택하세요.";
            return;
        }

        var result = await _local.DeleteCustomerCategoryAsync(SelectedCategoryOption.Id);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await _local.WaitForServerWriteAsync();
        await ReloadCustomerCategoriesAsync();
        NewCategoryOption();
    }

    [RelayCommand]
    private void NewPriceGradeOption()
    {
        SelectedPriceGradeOption = null;
        PriceGradeOptionName = string.Empty;
        PriceGradePriceSource = SelectionOptionDefaults.PriceSourceSales;
        PriceGradeOptionIsSystemDefault = false;
        IsNewPriceGradeOption = true;
    }

    [RelayCommand]
    private async Task SavePriceGradeOptionAsync()
    {
        var result = await _local.SavePriceGradeOptionAsync(
            new LocalPriceGradeOption
            {
                Id = IsNewPriceGradeOption ? Guid.NewGuid() : SelectedPriceGradeOption?.Id ?? Guid.NewGuid(),
                Name = PriceGradeOptionName,
                PriceSource = PriceGradePriceSource,
                SortOrder = IsNewPriceGradeOption ? PriceGradeOptions.Count * 10 : SelectedPriceGradeOption?.SortOrder ?? 0,
                IsSystemDefault = PriceGradeOptionIsSystemDefault
            },
            SelectedPriceGradeOption?.Name);

        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await _local.WaitForServerWriteAsync();
        await ReloadPriceGradeOptionsAsync();
        SelectedPriceGradeOption = PriceGradeOptions.FirstOrDefault(option => option.Id == result.EntityId);
    }

    [RelayCommand]
    private async Task DeletePriceGradeOptionAsync()
    {
        if (SelectedPriceGradeOption is null)
        {
            StatusMessage = "삭제할 가격등급을 선택하세요.";
            return;
        }

        var result = await _local.DeletePriceGradeOptionAsync(SelectedPriceGradeOption.Id);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await _local.WaitForServerWriteAsync();
        await ReloadPriceGradeOptionsAsync();
        NewPriceGradeOption();
    }

    [RelayCommand]
    private void NewTradeTypeOption()
    {
        SelectedTradeTypeOption = null;
        TradeTypeOptionName = string.Empty;
        TradeTypeAllowsSales = true;
        TradeTypeAllowsPurchase = false;
        TradeTypeOptionIsSystemDefault = false;
        IsNewTradeTypeOption = true;
    }

    [RelayCommand]
    private async Task SaveTradeTypeOptionAsync()
    {
        var result = await _local.SaveTradeTypeOptionAsync(
            new LocalTradeTypeOption
            {
                Id = IsNewTradeTypeOption ? Guid.NewGuid() : SelectedTradeTypeOption?.Id ?? Guid.NewGuid(),
                Name = TradeTypeOptionName,
                AllowsSales = TradeTypeAllowsSales,
                AllowsPurchase = TradeTypeAllowsPurchase,
                SortOrder = IsNewTradeTypeOption ? TradeTypeOptions.Count * 10 : SelectedTradeTypeOption?.SortOrder ?? 0,
                IsSystemDefault = TradeTypeOptionIsSystemDefault
            },
            SelectedTradeTypeOption?.Name);

        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await _local.WaitForServerWriteAsync();
        await ReloadTradeTypeOptionsAsync();
        SelectedTradeTypeOption = TradeTypeOptions.FirstOrDefault(option => option.Id == result.EntityId);
    }

    [RelayCommand]
    private async Task DeleteTradeTypeOptionAsync()
    {
        if (SelectedTradeTypeOption is null)
        {
            StatusMessage = "삭제할 거래구분을 선택하세요.";
            return;
        }

        var result = await _local.DeleteTradeTypeOptionAsync(SelectedTradeTypeOption.Id);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await _local.WaitForServerWriteAsync();
        await ReloadTradeTypeOptionsAsync();
        NewTradeTypeOption();
    }

    [RelayCommand]
    private void NewItemCategoryOption()
    {
        SelectedItemCategoryOption = null;
        ItemCategoryOptionName = string.Empty;
        ItemCategoryOptionIsSystemDefault = false;
        IsNewItemCategoryOption = true;
    }

    [RelayCommand]
    private async Task SaveItemCategoryOptionAsync()
    {
        var result = await _local.SaveItemCategoryOptionAsync(
            new LocalItemCategoryOption
            {
                Id = IsNewItemCategoryOption ? Guid.NewGuid() : SelectedItemCategoryOption?.Id ?? Guid.NewGuid(),
                Name = ItemCategoryOptionName,
                SortOrder = IsNewItemCategoryOption ? ItemCategoryOptions.Count * 10 : SelectedItemCategoryOption?.SortOrder ?? 0,
                IsSystemDefault = ItemCategoryOptionIsSystemDefault
            },
            SelectedItemCategoryOption?.Name);

        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await _local.WaitForServerWriteAsync();
        await ReloadItemCategoryOptionsAsync();
        SelectedItemCategoryOption = ItemCategoryOptions.FirstOrDefault(option => option.Id == result.EntityId);
    }

    [RelayCommand]
    private async Task DeleteItemCategoryOptionAsync()
    {
        if (SelectedItemCategoryOption is null)
        {
            StatusMessage = "삭제할 품목분류를 선택하세요.";
            return;
        }

        var result = await _local.DeleteItemCategoryOptionAsync(SelectedItemCategoryOption.Id);
        StatusMessage = result.Message;
        if (!result.Success)
            return;

        await _local.WaitForServerWriteAsync();
        await ReloadItemCategoryOptionsAsync();
        NewItemCategoryOption();
    }

    private async Task ReloadCustomerCategoriesAsync()
    {
        CustomerCategories.Clear();
        foreach (var category in await _local.GetCategoriesAsync())
            CustomerCategories.Add(category);
    }

    private async Task ReloadPriceGradeOptionsAsync()
    {
        PriceGradeOptions.Clear();
        foreach (var option in await _local.GetPriceGradeOptionsAsync())
            PriceGradeOptions.Add(option);
    }

    private async Task ReloadTradeTypeOptionsAsync()
    {
        TradeTypeOptions.Clear();
        foreach (var option in await _local.GetTradeTypeOptionsAsync())
            TradeTypeOptions.Add(option);
    }

    private async Task ReloadItemCategoryOptionsAsync()
    {
        ItemCategoryOptions.Clear();
        foreach (var option in await _local.GetItemCategoryOptionsAsync())
            ItemCategoryOptions.Add(option);
    }

    partial void OnSelectedCategoryOptionChanged(LocalCustomerCategory? value)
    {
        if (value is null)
            return;

        IsNewCategoryOption = false;
        CategoryOptionName = value.Name;
        CategoryOptionIsSystemDefault = value.IsSystemDefault;
    }

    partial void OnSelectedPriceGradeOptionChanged(LocalPriceGradeOption? value)
    {
        if (value is null)
            return;

        IsNewPriceGradeOption = false;
        PriceGradeOptionName = value.Name;
        PriceGradePriceSource = SelectionOptionDefaults.NormalizePriceSource(value.PriceSource);
        PriceGradeOptionIsSystemDefault = value.IsSystemDefault;
    }

    partial void OnSelectedTradeTypeOptionChanged(LocalTradeTypeOption? value)
    {
        if (value is null)
            return;

        IsNewTradeTypeOption = false;
        TradeTypeOptionName = value.Name;
        TradeTypeAllowsSales = value.AllowsSales;
        TradeTypeAllowsPurchase = value.AllowsPurchase;
        TradeTypeOptionIsSystemDefault = value.IsSystemDefault;
    }

    partial void OnSelectedItemCategoryOptionChanged(LocalItemCategoryOption? value)
    {
        if (value is null)
            return;

        IsNewItemCategoryOption = false;
        ItemCategoryOptionName = value.Name;
        ItemCategoryOptionIsSystemDefault = value.IsSystemDefault;
    }

    private static string NormalizeOfficeCode(string? officeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet);

    private static int GetOfficeSortOrder(string? officeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, DomainConstants.OfficeUsenet) switch
        {
            var code when string.Equals(code, OfficeCodeCatalog.Usenet, StringComparison.OrdinalIgnoreCase) => 0,
            var code when string.Equals(code, OfficeCodeCatalog.Itworld, StringComparison.OrdinalIgnoreCase) => 1,
            _ => 2
        };
}
