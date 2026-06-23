using System.Collections.Specialized;
using System.ComponentModel;
using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class ItemsPage : ContentPage
{
    private readonly ItemsViewModel _viewModel;
    private readonly MobileRefreshCoordinator _refreshCoordinator;
    private readonly SessionStore _sessionStore;
    private readonly Grid _categoryButtonGrid;
    private int _seenItemsVersion;

    public ItemsPage()
    {
        GeoraePlanTheme.ApplyPage(this, "품목");

        _viewModel = ServiceHelper.GetRequiredService<ItemsViewModel>();
        _refreshCoordinator = ServiceHelper.GetRequiredService<MobileRefreshCoordinator>();
        _sessionStore = ServiceHelper.GetRequiredService<SessionStore>();
        _refreshCoordinator.AllChanged += HandleRealtimeRefreshRequested;
        BindingContext = _viewModel;

        var canEditItems = _sessionStore.GetSnapshot().CanEditItems;

        _viewModel.ItemCategories.CollectionChanged += HandleCategoryCollectionChanged;
        _viewModel.PropertyChanged += HandleViewModelPropertyChanged;

        var categoryTitle = GeoraePlanTheme.CreateSectionTitle("품목분류", 15);
        var categoryGuide = GeoraePlanTheme.CreateBodyText("자주 쓰는 분류를 선택하면 해당 분류 품목만 빠르게 확인할 수 있습니다.", true, 12);
        categoryGuide.LineHeight = 1.0;
        var newItemFromCategoryButton = GeoraePlanTheme.CreateCompactButton("신규 품목", GeoraePlanTheme.Success);
        newItemFromCategoryButton.IsVisible = canEditItems;
        newItemFromCategoryButton.IsEnabled = canEditItems;
        newItemFromCategoryButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(OpenNewItemAsync, "품목 신규등록");

        _categoryButtonGrid = new Grid
        {
            ColumnSpacing = 10,
            RowSpacing = 10,
            IsVisible = true
        };
        _categoryButtonGrid.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.IsCategoryChooserVisible));

        var categoryCard = GeoraePlanTheme.CreateCompactCard(categoryTitle, categoryGuide, newItemFromCategoryButton, _categoryButtonGrid);
        categoryCard.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.IsCategoryChooserVisible));

        var selectedCategoryLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
        selectedCategoryLabel.FontAttributes = FontAttributes.Bold;
        selectedCategoryLabel.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedCategoryHeader));

        var changeCategoryButton = GeoraePlanTheme.CreateCompactButton("분류 다시 선택", GeoraePlanTheme.SecondaryButton);
        changeCategoryButton.Clicked += (_, _) => _viewModel.ClearSelectedCategory();

        var newItemButton = GeoraePlanTheme.CreateCompactButton("신규", GeoraePlanTheme.Success);
        newItemButton.IsVisible = canEditItems;
        newItemButton.IsEnabled = canEditItems;
        newItemButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(OpenNewItemAsync, "품목 신규등록");

        var categoryActions = new HorizontalStackLayout
        {
            Spacing = 6,
            HorizontalOptions = LayoutOptions.End,
            Children = { newItemButton, changeCategoryButton }
        };

        var selectedCategoryHeader = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            }
        };
        selectedCategoryHeader.Add(selectedCategoryLabel);
        selectedCategoryHeader.Add(categoryActions, 1, 0);
        selectedCategoryHeader.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.HasSelectedCategory));

        var categorySummary = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        categorySummary.LineHeight = 1.0;
        categorySummary.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedCategorySummary));

        var searchEntry = GeoraePlanTheme.CreateCompactEntry("품명 / 규격 검색");
        searchEntry.HeightRequest = 42;
        searchEntry.ReturnType = ReturnType.Search;
        searchEntry.HorizontalOptions = LayoutOptions.Fill;
        searchEntry.SetBinding(Entry.TextProperty, nameof(ItemsViewModel.SearchText));
        searchEntry.TextChanged += (_, args) =>
        {
            var text = args.NewTextValue ?? string.Empty;
            if (!string.Equals(_viewModel.SearchText, text, StringComparison.Ordinal))
                _viewModel.SearchText = text;
        };
        searchEntry.Completed += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await _viewModel.SearchItemsAsync(),
                "품목 작업");

        var searchButton = GeoraePlanTheme.CreateCompactButton("찾기", GeoraePlanTheme.SecondaryButton);
        searchButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await _viewModel.SearchItemsAsync(),
                "품목 작업");

        var clearSearchButton = GeoraePlanTheme.CreateCompactButton("초기화", GeoraePlanTheme.SecondaryButton);
        clearSearchButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await _viewModel.ClearSearchAsync(),
                "품목 검색 초기화");

        var searchGrid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(76)),
                new ColumnDefinition(new GridLength(76))
            }
        };
        searchGrid.Add(searchEntry);
        searchGrid.Add(searchButton, 1, 0);
        searchGrid.Add(clearSearchButton, 2, 0);

        var searchTitle = GeoraePlanTheme.CreateSectionTitle("품목 검색", 15);
        var searchGuide = GeoraePlanTheme.CreateBodyText("분류를 고르지 않아도 전체 품목에서 품명, 규격, 자재번호를 검색합니다.", true, 12);
        searchGuide.LineHeight = 1.0;
        var searchCard = GeoraePlanTheme.CreateCompactCard(searchTitle, searchGuide, searchGrid);

        var itemListLabel = GeoraePlanTheme.CreateFieldLabel("분류 품목 목록");
        itemListLabel.SetBinding(Label.TextProperty, nameof(ItemsViewModel.ItemListLabelText));
        itemListLabel.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.CanShowItemList));

        var itemList = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("표시할 품목이 없습니다. 검색어를 바꾸거나 분류를 선택하세요.", true, 12),
            ItemTemplate = new DataTemplate(() =>
            {
                var nameLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
                nameLabel.FontAttributes = FontAttributes.Bold;
                nameLabel.LineHeight = 1.0;
                nameLabel.SetBinding(Label.TextProperty, nameof(ItemDto.NameOriginal));

                var specLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                specLabel.LineHeight = 1.0;
                specLabel.SetBinding(Label.TextProperty, nameof(ItemDto.SpecificationOriginal));

                var priceLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                priceLabel.LineHeight = 1.0;
                priceLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new ItemPriceConverter()));

                var stockLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                stockLabel.LineHeight = 1.0;
                stockLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new ItemStockConverter()));

                var border = new Border
                {
                    BackgroundColor = GeoraePlanTheme.Surface,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = new Thickness(10, 9),
                    Margin = new Thickness(0, 0, 0, 6),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { nameLabel, specLabel, priceLabel, stockLabel }
                    }
                };

                var tap = new TapGestureRecognizer();
                tap.Tapped += (sender, _) =>
                    MobileErrorHandler.FireAndForget(
                        async () =>
                        {
                    if (sender is Border card && card.BindingContext is ItemDto item)
                        await _viewModel.SelectItemAsync(item);
                },
                        "품목 작업");
                border.GestureRecognizers.Add(tap);
                return border;
            })
        };
        itemList.SetBinding(ItemsView.ItemsSourceProperty, nameof(ItemsViewModel.Items));
        itemList.SetBinding(VisualElement.HeightRequestProperty, nameof(ItemsViewModel.ItemListHeight));
        itemList.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.CanShowItemList));

        var detailTitle = GeoraePlanTheme.CreateSectionTitle(string.Empty, 15);
        detailTitle.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedItemTitle));

        var detailSpecification = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        detailSpecification.LineHeight = 1.0;
        detailSpecification.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedItemSpecification));

        var detailIdentity = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        detailIdentity.LineHeight = 1.0;
        detailIdentity.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedItemIdentitySummary));

        var detailPrice = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        detailPrice.LineHeight = 1.0;
        detailPrice.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedItemPriceSummary));

        var detailStock = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        detailStock.LineHeight = 1.0;
        detailStock.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedItemStockSummary));

        var detailMemo = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        detailMemo.LineHeight = 1.0;
        detailMemo.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedItemMemo));

        var editItemButton = GeoraePlanTheme.CreateCompactButton("수정", GeoraePlanTheme.Purple);
        editItemButton.IsVisible = canEditItems;
        editItemButton.IsEnabled = canEditItems;
        editItemButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(OpenEditItemAsync, "품목 수정");

        var deleteItemButton = GeoraePlanTheme.CreateCompactButton("삭제", GeoraePlanTheme.Danger);
        deleteItemButton.IsVisible = canEditItems;
        deleteItemButton.IsEnabled = canEditItems;
        deleteItemButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(OpenDeleteItemAsync, "품목 삭제");

        var detailActions = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            }
        };
        detailActions.Add(editItemButton, 0, 0);
        detailActions.Add(deleteItemButton, 1, 0);

        var branchStockLabel = GeoraePlanTheme.CreateFieldLabel("지점별 재고");
        branchStockLabel.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.HasSelectedItem));

        var branchStockView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("지점별 재고 정보가 없습니다.", true, 11),
            ItemTemplate = new DataTemplate(() =>
            {
                var warehouseLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 11);
                warehouseLabel.SetBinding(Label.TextProperty, new Binding(nameof(ItemWarehouseStockDto.WarehouseCode), converter: new WarehouseDisplayNameConverter()));

                var quantityLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                quantityLabel.SetBinding(Label.TextProperty, new Binding(nameof(ItemWarehouseStockDto.Quantity), stringFormat: "재고 {0:N0}"));

                var rowGrid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Auto)
                    }
                };
                rowGrid.Add(warehouseLabel);
                rowGrid.Add(quantityLabel, 1, 0);
                return rowGrid;
            })
        };
        branchStockView.SetBinding(ItemsView.ItemsSourceProperty, nameof(ItemsViewModel.SelectedItemBranchStocks));
        branchStockView.SetBinding(VisualElement.HeightRequestProperty, nameof(ItemsViewModel.SelectedItemBranchStocksHeight));
        branchStockView.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.HasSelectedItem));

        var detailCard = GeoraePlanTheme.CreateCompactCard(
            detailTitle,
            detailSpecification,
            detailIdentity,
            detailPrice,
            detailStock,
            detailMemo,
            detailActions,
            branchStockLabel,
            branchStockView);
        detailCard.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.HasSelectedItem));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(ItemsViewModel.StatusMessage));

        var listCard = GeoraePlanTheme.CreateCompactCard(
            selectedCategoryHeader,
            categorySummary,
            itemListLabel,
            itemList);
        listCard.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.CanShowItemList));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 12,
                Spacing = 12,
                Children =
                {
                    searchCard,
                    categoryCard,
                    listCard,
                    detailCard,
                    statusLabel
                }
            }
        };

        RebuildCategoryButtons();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        await MobileErrorHandler.RunGuardedAsync(
            async () =>
            {
try
        {
            await _viewModel.PrepareForEntryAsync();
            RebuildCategoryButtons();
            _seenItemsVersion = _refreshCoordinator.ItemsVersion;
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"품목 화면 진입 실패: {ex.Message}";
        }
            },
            "품목 화면 초기화");
    }

    private void HandleRealtimeRefreshRequested(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
            MobileErrorHandler.FireAndForget(
                async () =>
                {
                    if (Shell.Current?.CurrentPage == this && _seenItemsVersion != _refreshCoordinator.ItemsVersion)
                    {
                        await _viewModel.PrepareForEntryAsync();
                        RebuildCategoryButtons();
                        _seenItemsVersion = _refreshCoordinator.ItemsVersion;
                    }
                },
                "품목 실시간 갱신"));
    }

    protected override bool OnBackButtonPressed()
    {
        if (_viewModel.TryNavigateBackOneStep())
            return true;

        return base.OnBackButtonPressed();
    }

    private void HandleCategoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildCategoryButtons();

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ItemsViewModel.SelectedCategory) or nameof(ItemsViewModel.HasSelectedCategory))
            RebuildCategoryButtons();
    }

    private void RebuildCategoryButtons()
    {
        _categoryButtonGrid.Children.Clear();
        _categoryButtonGrid.RowDefinitions.Clear();
        _categoryButtonGrid.ColumnDefinitions.Clear();

        const int columnCount = 3;
        for (var column = 0; column < columnCount; column++)
            _categoryButtonGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));

        var categories = _viewModel.ItemCategories.ToList();
        if (categories.Count == 0)
            return;

        for (var index = 0; index < categories.Count; index++)
        {
            var row = index / columnCount;
            var column = index % columnCount;
            while (_categoryButtonGrid.RowDefinitions.Count <= row)
                _categoryButtonGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var category = categories[index];
            var isSelected = _viewModel.SelectedCategory is not null &&
                             string.Equals(_viewModel.SelectedCategory.Name, category.Name, StringComparison.OrdinalIgnoreCase);
            var button = GeoraePlanTheme.CreateButton(category.Name, isSelected ? GeoraePlanTheme.Accent : GeoraePlanTheme.SecondaryButton);
            button.HeightRequest = 48;
            button.CornerRadius = 12;
            button.Padding = new Thickness(10, 4);
            button.Clicked += (_, _) =>
                MobileErrorHandler.FireAndForget(
                    async () => await _viewModel.SelectCategoryAsync(category),
                    "품목 작업");
            _categoryButtonGrid.Add(button, column, row);
        }
    }

    private async Task OpenNewItemAsync()
    {
        if (!await EnsureCanEditItemsAsync("신규등록"))
            return;

        await Navigation.PushModalAsync(new ItemEditPage(
            null,
            _viewModel.SelectedCategory?.Name,
            async saved =>
            {
                await _viewModel.RefreshAsync();
                RebuildCategoryButtons();
                if (saved is not null)
                    await _viewModel.SelectItemAsync(saved);
            }));
    }

    private async Task OpenEditItemAsync()
    {
        if (_viewModel.SelectedItem is null)
            return;

        if (!await EnsureCanEditItemsAsync("수정"))
            return;

        var editedItemId = _viewModel.SelectedItem.Id;
        await Navigation.PushModalAsync(new ItemEditPage(
            _viewModel.SelectedItem,
            _viewModel.SelectedCategory?.Name,
            async saved =>
            {
                if (saved is null || saved.IsDeleted)
                {
                    _viewModel.RemoveDeletedItemFromCurrentView(editedItemId);
                    RebuildCategoryButtons();
                    return;
                }

                await _viewModel.RefreshAsync();
                RebuildCategoryButtons();
                await _viewModel.SelectItemAsync(saved);
            }));
    }

    private async Task OpenDeleteItemAsync()
    {
        if (_viewModel.SelectedItem is null)
            return;

        if (!await EnsureCanEditItemsAsync("삭제"))
            return;

        var deletedItemId = _viewModel.SelectedItem.Id;
        await Navigation.PushModalAsync(new ItemEditPage(
            _viewModel.SelectedItem,
            _viewModel.SelectedCategory?.Name,
            async saved =>
            {
                if (saved?.IsDeleted == true)
                {
                    _viewModel.RemoveDeletedItemFromCurrentView(deletedItemId);
                    RebuildCategoryButtons();
                    return;
                }

                await _viewModel.RefreshAsync();
                RebuildCategoryButtons();
            }));
    }

    private async Task<bool> EnsureCanEditItemsAsync(string actionText)
    {
        if (_sessionStore.GetSnapshot().CanEditItems)
            return true;

        var message = $"권한이 없어 품목을 {actionText}할 수 없습니다.";
        _viewModel.StatusMessage = message;
        await DisplayAlert("권한 확인", message, "확인");
        return false;
    }

    private sealed class ItemPriceConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not ItemDto item)
                return string.Empty;

            var displayPrice = item.SalePrice > 0m
                ? item.SalePrice
                : item.RetailPrice > 0m
                    ? item.RetailPrice
                    : item.PurchasePrice;
            return displayPrice > 0m ? $"기본 단가 {displayPrice:N0}원" : "기본 단가 미등록";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class ItemStockConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not ItemDto item)
                return string.Empty;

            var unit = string.IsNullOrWhiteSpace(item.Unit) ? "EA" : item.Unit;
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.CategoryName))
                parts.Add(item.CategoryName.Trim());
            if (!string.IsNullOrWhiteSpace(item.ItemKind))
                parts.Add(item.ItemKind.Trim());
            if (!string.IsNullOrWhiteSpace(item.TrackingType))
                parts.Add(item.TrackingType.Trim());
            if (!string.IsNullOrWhiteSpace(item.MaterialNumber))
                parts.Add($"자재 {item.MaterialNumber.Trim()}");
            if (!string.IsNullOrWhiteSpace(item.SerialNumber))
                parts.Add($"S/N {item.SerialNumber.Trim()}");
            parts.Add($"단위 {unit}");
            parts.Add($"현재재고 {item.CurrentStock:N0}");

            return string.Join(" · ", parts);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class WarehouseDisplayNameConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => WarehouseDisplayNameResolver.Resolve(value?.ToString());

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}
