using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.ViewModels;

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

    public ObservableCollection<LocalCustomerCategory> CustomerCategories { get; } = new();
    public ObservableCollection<LocalPriceGradeOption> PriceGradeOptions { get; } = new();
    public ObservableCollection<LocalTradeTypeOption> TradeTypeOptions { get; } = new();
    public ObservableCollection<LocalItemCategoryOption> ItemCategoryOptions { get; } = new();
    public ObservableCollection<DisplayOption> UserOfficeOptions { get; } = new();

    public IReadOnlyList<DisplayOption> RoleOptions { get; } =
    [
        new() { Value = "Admin", DisplayName = "관리자" },
        new() { Value = "User", DisplayName = "일반" }
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
        RefreshUserOfficeOptions();
        NewCategoryOption();
        NewPriceGradeOption();
        NewTradeTypeOption();
        NewItemCategoryOption();
    }

    private void RefreshUserOfficeOptions()
    {
        UserOfficeOptions.Clear();
        foreach (var office in Offices
                     .OrderBy(current => GetOfficeSortOrder(current.Code))
                     .ThenBy(current => current.Code, StringComparer.OrdinalIgnoreCase))
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

    private void SetDefaultEditingUserOfficeCode()
    {
        var currentCode = NormalizeOfficeCode(EditingUserOfficeCode);
        if (UserOfficeOptions.Any(option => string.Equals(option.Value, currentCode, StringComparison.OrdinalIgnoreCase)))
        {
            EditingUserOfficeCode = currentCode;
            return;
        }

        EditingUserOfficeCode = UserOfficeOptions.FirstOrDefault()?.Value
            ?? NormalizeOfficeCode(_session.OfficeCode)
            ?? DomainConstants.OfficeUsenet;
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
