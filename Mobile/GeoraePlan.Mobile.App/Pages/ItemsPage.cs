using System.Collections.Specialized;
using System.ComponentModel;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Layouts;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class ItemsPage : ContentPage
{
    private readonly ItemsViewModel _viewModel;
    private readonly MobileRefreshCoordinator _refreshCoordinator;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly Grid _categoryButtonGrid;
    private readonly FlexLayout _recentItemsLayout;
    private int _seenItemsVersion;

    public ItemsPage()
    {
        GeoraePlanTheme.ApplyPage(this, "품목");

        _viewModel = ServiceHelper.GetRequiredService<ItemsViewModel>();
        _refreshCoordinator = ServiceHelper.GetRequiredService<MobileRefreshCoordinator>();
        _syncCoordinator = ServiceHelper.GetRequiredService<SyncCoordinator>();
        BindingContext = _viewModel;

        _viewModel.ItemCategories.CollectionChanged += HandleCategoryCollectionChanged;
        _viewModel.VisibleRecentItems.CollectionChanged += HandleRecentCollectionChanged;
        _viewModel.PropertyChanged += HandleViewModelPropertyChanged;

        var categoryTitle = GeoraePlanTheme.CreateSectionTitle("품목분류", 15);
        var categoryGuide = GeoraePlanTheme.CreateBodyText("분류를 먼저 선택한 뒤 같은 화면에서 연속으로 품목을 추가하세요.", true, 12);
        categoryGuide.LineHeight = 1.0;

        _categoryButtonGrid = new Grid
        {
            ColumnSpacing = 10,
            RowSpacing = 10,
            IsVisible = true
        };
        _categoryButtonGrid.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.IsCategoryChooserVisible));

        var categoryCard = GeoraePlanTheme.CreateCompactCard(categoryTitle, categoryGuide, _categoryButtonGrid);
        categoryCard.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.IsCategoryChooserVisible));

        var selectedCategoryLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
        selectedCategoryLabel.FontAttributes = FontAttributes.Bold;
        selectedCategoryLabel.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedCategoryHeader));

        var changeCategoryButton = GeoraePlanTheme.CreateCompactButton("분류 변경", GeoraePlanTheme.SecondaryButton);
        changeCategoryButton.Clicked += (_, _) => _viewModel.ClearSelectedCategory();

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
        selectedCategoryHeader.Add(changeCategoryButton, 1, 0);
        selectedCategoryHeader.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.HasSelectedCategory));

        var categorySummary = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        categorySummary.LineHeight = 1.0;
        categorySummary.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedCategorySummary));
        categorySummary.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.HasSelectedCategory));

        var searchEntry = GeoraePlanTheme.CreateCompactEntry("품목명 / 규격 검색");
        searchEntry.SetBinding(Entry.TextProperty, nameof(ItemsViewModel.SearchText));
        searchEntry.Completed += async (_, _) => await _viewModel.SearchItemsAsync();

        var searchButton = GeoraePlanTheme.CreateCompactButton("찾기", GeoraePlanTheme.SecondaryButton);
        searchButton.Clicked += async (_, _) => await _viewModel.SearchItemsAsync();

        var searchGrid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(92))
            }
        };
        searchGrid.Add(searchEntry);
        searchGrid.Add(searchButton, 1, 0);
        searchGrid.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.HasSelectedCategory));

        var recentHeader = GeoraePlanTheme.CreateFieldLabel("최근 선택 품목");
        recentHeader.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.HasVisibleRecentItems));

        _recentItemsLayout = new FlexLayout
        {
            Wrap = FlexWrap.Wrap,
            Direction = FlexDirection.Row,
            JustifyContent = FlexJustify.Start,
            AlignItems = FlexAlignItems.Start
        };

        var recentScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            Content = _recentItemsLayout
        };
        recentScroll.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.HasVisibleRecentItems));

        var itemListLabel = GeoraePlanTheme.CreateFieldLabel("현재 분류 품목");
        itemListLabel.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.HasSelectedCategory));

        var itemList = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("현재 분류에 표시할 품목이 없습니다.", true, 12),
            ItemTemplate = new DataTemplate(() =>
            {
                var nameLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
                nameLabel.FontAttributes = FontAttributes.Bold;
                nameLabel.LineHeight = 1.0;
                nameLabel.SetBinding(Label.TextProperty, nameof(ItemDto.NameOriginal));

                var specLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                specLabel.LineHeight = 1.0;
                specLabel.SetBinding(Label.TextProperty, nameof(ItemDto.SpecificationOriginal));

                var metaLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                metaLabel.LineHeight = 1.0;
                metaLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new ItemMetaConverter()));

                var hintLabel = GeoraePlanTheme.CreateBodyText("탭하면 하단 시트에서 수량/단가를 입력합니다.", true, 11);
                hintLabel.LineHeight = 1.0;

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
                        Children = { nameLabel, specLabel, metaLabel, hintLabel }
                    }
                };

                var tap = new TapGestureRecognizer();
                tap.Tapped += async (sender, _) =>
                {
                    if (sender is Border card && card.BindingContext is ItemDto item)
                        await _viewModel.SelectItemAsync(item);
                };
                border.GestureRecognizers.Add(tap);
                return border;
            })
        };
        itemList.SetBinding(ItemsView.ItemsSourceProperty, nameof(ItemsViewModel.Items));
        itemList.SetBinding(VisualElement.HeightRequestProperty, nameof(ItemsViewModel.ItemListHeight));
        itemList.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.HasSelectedCategory));

        var categoryContentCard = GeoraePlanTheme.CreateCompactCard(
            selectedCategoryHeader,
            categorySummary,
            searchGrid,
            recentHeader,
            recentScroll,
            itemListLabel,
            itemList);
        categoryContentCard.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.HasSelectedCategory));

        var draftTitle = GeoraePlanTheme.CreateSectionTitle("전표 목록", 15);
        var draftSummary = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
        draftSummary.LineHeight = 1.0;
        draftSummary.SetBinding(Label.TextProperty, nameof(ItemsViewModel.DraftSummary));

        var draftList = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("추가된 품목이 없습니다.", true, 12),
            ItemTemplate = new DataTemplate(() =>
            {
                var nameLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
                nameLabel.FontAttributes = FontAttributes.Bold;
                nameLabel.LineHeight = 1.0;
                nameLabel.SetBinding(Label.TextProperty, nameof(InvoiceLineDraftItem.ItemNameOriginal));

                var specLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                specLabel.LineHeight = 1.0;
                specLabel.SetBinding(Label.TextProperty, new Binding(nameof(InvoiceLineDraftItem.SpecificationOriginal), stringFormat: "규격 {0}"));

                var summaryLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                summaryLabel.LineHeight = 1.0;
                summaryLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new DraftLineSummaryConverter()));

                var removeButton = GeoraePlanTheme.CreateCompactButton("삭제", GeoraePlanTheme.Danger);
                removeButton.Clicked += async (sender, _) =>
                {
                    if (sender is Button button && button.BindingContext is InvoiceLineDraftItem line)
                        await _viewModel.RemoveDraftLineAsync(line);
                };

                var rowGrid = new Grid
                {
                    ColumnSpacing = 10,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Auto)
                    }
                };
                rowGrid.Add(new VerticalStackLayout
                {
                    Spacing = 4,
                    Children = { nameLabel, specLabel, summaryLabel }
                });
                rowGrid.Add(removeButton, 1, 0);

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.Surface,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = new Thickness(10, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Content = rowGrid
                };
            })
        };
        draftList.SetBinding(ItemsView.ItemsSourceProperty, nameof(ItemsViewModel.DraftLines));
        draftList.SetBinding(VisualElement.HeightRequestProperty, nameof(ItemsViewModel.DraftLinesHeight));

        var draftCard = GeoraePlanTheme.CreateCompactCard(draftTitle, draftSummary, draftList);

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(ItemsViewModel.StatusMessage));

        var stack = new VerticalStackLayout
        {
            Spacing = 12,
            Children =
            {
                categoryCard,
                categoryContentCard,
                draftCard,
                statusLabel
            }
        };

        var scroll = new ScrollView
        {
            Content = new Grid
            {
                Padding = 12,
                Children = { stack }
            }
        };

        var backdrop = new BoxView
        {
            BackgroundColor = Color.FromArgb("#80000000"),
            IsVisible = false
        };
        backdrop.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.IsItemEntrySheetVisible));

        var sheetTitle = GeoraePlanTheme.CreateSectionTitle(string.Empty, 15);
        sheetTitle.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedItemSheetTitle));

        var sheetSpec = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        sheetSpec.LineHeight = 1.0;
        sheetSpec.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedItemSheetSpecification));

        var sheetPrice = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        sheetPrice.LineHeight = 1.0;
        sheetPrice.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedItemPriceSummary));

        var sheetStock = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        sheetStock.LineHeight = 1.0;
        sheetStock.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedItemStockSummary));

        var sheetMemo = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        sheetMemo.LineHeight = 1.0;
        sheetMemo.SetBinding(Label.TextProperty, nameof(ItemsViewModel.SelectedItemMemo));

        var branchStockView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("지점별 재고 없음", true, 11),
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

        var quantityLabel = GeoraePlanTheme.CreateFieldLabel("수량");
        var quantityEntry = GeoraePlanTheme.CreateCompactEntry("수량");
        quantityEntry.Keyboard = Keyboard.Numeric;
        quantityEntry.SetBinding(Entry.TextProperty, nameof(ItemsViewModel.LineQuantityText));

        var unitPriceLabel = GeoraePlanTheme.CreateFieldLabel("단가");
        var unitPriceEntry = GeoraePlanTheme.CreateCompactEntry("단가");
        unitPriceEntry.Keyboard = Keyboard.Numeric;
        unitPriceEntry.SetBinding(Entry.TextProperty, nameof(ItemsViewModel.LineUnitPriceText));

        var remarkLabel = GeoraePlanTheme.CreateFieldLabel("메모");
        var remarkEntry = GeoraePlanTheme.CreateCompactEntry("메모");
        remarkEntry.SetBinding(Entry.TextProperty, nameof(ItemsViewModel.LineRemark));

        var quantityColumn = new VerticalStackLayout { Spacing = 4, Children = { quantityLabel, quantityEntry } };
        var unitPriceColumn = new VerticalStackLayout { Spacing = 4, Children = { unitPriceLabel, unitPriceEntry } };

        var entryGrid = new Grid
        {
            ColumnSpacing = 10,
            RowSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            }
        };
        entryGrid.Add(quantityColumn);
        entryGrid.Add(unitPriceColumn, 1, 0);
        var remarkColumn = new VerticalStackLayout { Spacing = 4, Children = { remarkLabel, remarkEntry } };
        entryGrid.Add(remarkColumn, 0, 1);
        entryGrid.SetColumnSpan(remarkColumn, 2);

        var addButton = GeoraePlanTheme.CreateCompactButton("품목 추가", GeoraePlanTheme.Success);
        addButton.Clicked += async (_, _) => await _viewModel.AddDraftLineAsync();

        var cancelButton = GeoraePlanTheme.CreateCompactButton("취소", GeoraePlanTheme.SecondaryButton);
        cancelButton.Clicked += async (_, _) => await _viewModel.CancelItemEntryAsync();

        var actionGrid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            }
        };
        actionGrid.Add(addButton);
        actionGrid.Add(cancelButton, 1, 0);

        var bottomSheet = new Border
        {
            BackgroundColor = GeoraePlanTheme.Surface,
            Stroke = GeoraePlanTheme.Border,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(18, 18, 0, 0) },
            Padding = new Thickness(14, 12),
            VerticalOptions = LayoutOptions.End,
            IsVisible = false,
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    sheetTitle,
                    sheetSpec,
                    sheetPrice,
                    sheetStock,
                    sheetMemo,
                    branchStockView,
                    entryGrid,
                    actionGrid
                }
            }
        };
        bottomSheet.SetBinding(VisualElement.IsVisibleProperty, nameof(ItemsViewModel.IsItemEntrySheetVisible));

        var backdropTap = new TapGestureRecognizer();
        backdropTap.Tapped += async (_, _) => await _viewModel.CancelItemEntryAsync();
        backdrop.GestureRecognizers.Add(backdropTap);

        var root = new Grid();
        root.Children.Add(scroll);
        root.Children.Add(backdrop);
        root.Children.Add(bottomSheet);

        Content = root;

        RebuildCategoryButtons();
        RebuildRecentItems();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _syncCoordinator.TryBackgroundSyncAsync("items-page", TimeSpan.FromSeconds(45));

        var versionChanged = _seenItemsVersion != _refreshCoordinator.ItemsVersion;
        if (versionChanged || _viewModel.NeedsRefresh(TimeSpan.FromSeconds(30)))
            await _viewModel.RefreshAsync();

        _seenItemsVersion = _refreshCoordinator.ItemsVersion;
        RebuildCategoryButtons();
        RebuildRecentItems();
    }

    private void HandleCategoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildCategoryButtons();

    private void HandleRecentCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildRecentItems();

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ItemsViewModel.SelectedCategory) or nameof(ItemsViewModel.HasSelectedCategory))
        {
            RebuildCategoryButtons();
            RebuildRecentItems();
        }
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
            button.Clicked += async (_, _) => await _viewModel.SelectCategoryAsync(category);
            _categoryButtonGrid.Add(button, column, row);
        }
    }

    private void RebuildRecentItems()
    {
        _recentItemsLayout.Children.Clear();
        foreach (var recent in _viewModel.VisibleRecentItems)
        {
            var matchesCurrentCategory = _viewModel.SelectedCategory is not null &&
                                         string.Equals(_viewModel.SelectedCategory.Name, recent.CategoryName, StringComparison.OrdinalIgnoreCase);
            var button = GeoraePlanTheme.CreateCompactButton(recent.ItemNameOriginal, matchesCurrentCategory ? GeoraePlanTheme.Accent : GeoraePlanTheme.SecondaryButton);
            button.Margin = new Thickness(0, 0, 8, 8);
            button.Padding = new Thickness(12, 0);
            button.Clicked += async (_, _) => await _viewModel.SelectRecentItemAsync(recent);
            _recentItemsLayout.Children.Add(button);
        }
    }

    private sealed class ItemMetaConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not ItemDto item)
                return string.Empty;

            var unit = string.IsNullOrWhiteSpace(item.Unit) ? "EA" : item.Unit;
            return $"단위 {unit} · 현재재고 {item.CurrentStock:N0}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class DraftLineSummaryConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not InvoiceLineDraftItem line)
                return string.Empty;

            return $"수량 {line.Quantity:N0} / 단가 {line.UnitPrice:N0}원 / 합계 {line.LineAmount:N0}원";
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
