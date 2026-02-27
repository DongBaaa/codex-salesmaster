using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class InventoryViewModel : ObservableObject
{
    private readonly LocalStateService _local;
    private List<LocalItem> _allItems = new();

    // ── 목록 ─────────────────────────────────────────────────────────────
    public ObservableCollection<LocalItem> FilteredItems { get; } = new();
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private LocalItem? _selectedItem;
    [ObservableProperty] private int _totalCount;

    // ── 편집 폼 ──────────────────────────────────────────────────────────
    [ObservableProperty] private Guid _editId = Guid.NewGuid();
    [ObservableProperty] private string _editName = string.Empty;
    [ObservableProperty] private string _editCategoryName = string.Empty;
    [ObservableProperty] private string _editSpec = string.Empty;
    [ObservableProperty] private string _editUnit = string.Empty;
    [ObservableProperty] private decimal _editBoxQty;
    [ObservableProperty] private string _editStorageLocation = string.Empty;
    [ObservableProperty] private decimal _editCurrentStock;
    [ObservableProperty] private decimal _editSafetyStock;
    [ObservableProperty] private decimal _editPurchasePrice;
    [ObservableProperty] private decimal _editSalePrice;
    [ObservableProperty] private decimal _editRetailPrice;
    [ObservableProperty] private decimal _editPriceA;
    [ObservableProperty] private decimal _editPriceB;
    [ObservableProperty] private decimal _editPriceC;
    [ObservableProperty] private DateOnly? _editLastPurchaseDate;
    [ObservableProperty] private DateOnly? _editLastSaleDate;
    [ObservableProperty] private string _editSimpleMemo = string.Empty;
    [ObservableProperty] private bool _editIsSale = true;
    [ObservableProperty] private bool _editIsRental;

    // ── 계산값 ────────────────────────────────────────────────────────────
    public decimal BoxCurrentStock => EditBoxQty > 0 ? Math.Floor(EditCurrentStock / EditBoxQty) : 0;
    public decimal AssetValue => EditCurrentStock * EditPurchasePrice;
    public decimal ShortageStock => EditCurrentStock < EditSafetyStock ? EditSafetyStock - EditCurrentStock : 0;

    // ── 상태 ─────────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isNew = true;

    public InventoryViewModel(LocalStateService local)
    {
        _local = local;
    }

    public async Task LoadAsync()
    {
        _allItems = await _local.GetItemsAsync();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var text = SearchText.Trim();
        FilteredItems.Clear();
        var list = string.IsNullOrEmpty(text)
            ? _allItems
            : _allItems.Where(i =>
                i.NameOriginal.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                i.CategoryName.Contains(text, StringComparison.OrdinalIgnoreCase));
        foreach (var i in list)
            FilteredItems.Add(i);
        TotalCount = FilteredItems.Count;
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedItemChanged(LocalItem? value)
    {
        if (value is null) return;
        LoadFormFromItem(value);
    }

    private void LoadFormFromItem(LocalItem i)
    {
        IsNew = false;
        EditId = i.Id;
        EditName = i.NameOriginal;
        EditCategoryName = i.CategoryName;
        EditSpec = i.SpecificationOriginal;
        EditUnit = i.Unit;
        EditBoxQty = i.BoxQuantity;
        EditStorageLocation = i.StorageLocation;
        EditCurrentStock = i.CurrentStock;
        EditSafetyStock = i.SafetyStock;
        EditPurchasePrice = i.PurchasePrice;
        EditSalePrice = i.SalePrice;
        EditRetailPrice = i.RetailPrice;
        EditPriceA = i.PriceGradeA;
        EditPriceB = i.PriceGradeB;
        EditPriceC = i.PriceGradeC;
        EditLastPurchaseDate = i.LastPurchaseDate;
        EditLastSaleDate = i.LastSaleDate;
        EditSimpleMemo = i.SimpleMemo;
        EditIsSale = i.IsSale;
        EditIsRental = i.IsRental;
        OnPropertyChanged(nameof(BoxCurrentStock));
        OnPropertyChanged(nameof(AssetValue));
        OnPropertyChanged(nameof(ShortageStock));
    }

    [RelayCommand]
    private void NewItem()
    {
        IsNew = true;
        SelectedItem = null;
        EditId = Guid.NewGuid();
        EditName = EditCategoryName = EditSpec = EditUnit = EditStorageLocation = EditSimpleMemo = string.Empty;
        EditBoxQty = EditCurrentStock = EditSafetyStock = 0;
        EditPurchasePrice = EditSalePrice = EditRetailPrice = EditPriceA = EditPriceB = EditPriceC = 0;
        EditLastPurchaseDate = EditLastSaleDate = null;
        EditIsSale = true;
        EditIsRental = false;
        OnPropertyChanged(nameof(BoxCurrentStock));
        OnPropertyChanged(nameof(AssetValue));
    }

    [RelayCommand]
    private async Task SaveItemAsync()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            StatusMessage = "품명을 입력하세요.";
            return;
        }
        var item = new LocalItem
        {
            Id = EditId,
            NameOriginal = EditName,
            NameMatchKey = EditName.ToUpperInvariant(),
            CategoryName = EditCategoryName,
            SpecificationOriginal = EditSpec,
            SpecificationMatchKey = EditSpec.ToUpperInvariant(),
            Unit = EditUnit,
            BoxQuantity = EditBoxQty,
            StorageLocation = EditStorageLocation,
            CurrentStock = EditCurrentStock,
            SafetyStock = EditSafetyStock,
            PurchasePrice = EditPurchasePrice,
            SalePrice = EditSalePrice,
            RetailPrice = EditRetailPrice,
            PriceGradeA = EditPriceA,
            PriceGradeB = EditPriceB,
            PriceGradeC = EditPriceC,
            LastPurchaseDate = EditLastPurchaseDate,
            LastSaleDate = EditLastSaleDate,
            SimpleMemo = EditSimpleMemo,
            IsSale = EditIsSale,
            IsRental = EditIsRental,
        };
        await _local.UpsertItemAsync(item);
        await LoadAsync();

        // 저장 후 방금 저장한 항목 재선택
        SelectedItem = FilteredItems.FirstOrDefault(x => x.Id == EditId);
        StatusMessage = "저장되었습니다.";
        IsNew = false;
    }

    [RelayCommand]
    private async Task DeleteItemAsync()
    {
        if (SelectedItem is null) return;
        await _local.DeleteItemAsync(SelectedItem.Id);
        await LoadAsync();
        NewItem();
        StatusMessage = "삭제되었습니다.";
    }

    partial void OnEditCurrentStockChanged(decimal value)
    {
        OnPropertyChanged(nameof(BoxCurrentStock));
        OnPropertyChanged(nameof(AssetValue));
        OnPropertyChanged(nameof(ShortageStock));
    }
    partial void OnEditPurchasePriceChanged(decimal value) => OnPropertyChanged(nameof(AssetValue));
    partial void OnEditBoxQtyChanged(decimal value) => OnPropertyChanged(nameof(BoxCurrentStock));
    partial void OnEditSafetyStockChanged(decimal value) => OnPropertyChanged(nameof(ShortageStock));
}
