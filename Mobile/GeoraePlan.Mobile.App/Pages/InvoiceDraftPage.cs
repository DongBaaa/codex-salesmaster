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

public sealed class InvoiceDraftPage : ContentPage
{
    private readonly InvoiceDraftViewModel _viewModel;
    private readonly Guid? _preferredCustomerId;
    private readonly string _preferredCustomerName;
    private readonly Grid _categoryButtonGrid;
    private readonly FlexLayout _recentItemsLayout;

    public InvoiceDraftPage(Guid? preferredCustomerId = null, string? preferredCustomerName = null)
    {
        GeoraePlanTheme.ApplyPage(this, "전표 작성");

        _viewModel = ServiceHelper.GetRequiredService<InvoiceDraftViewModel>();
        _viewModel.SavedSuccessfully += HandleSavedSuccessfullyAsync;
        _viewModel.PropertyChanged += HandleViewModelPropertyChanged;
        _viewModel.ItemCategories.CollectionChanged += HandleCategoryCollectionChanged;
        _viewModel.VisibleRecentItems.CollectionChanged += HandleRecentCollectionChanged;
        BindingContext = _viewModel;
        _preferredCustomerId = preferredCustomerId;
        _preferredCustomerName = preferredCustomerName?.Trim() ?? string.Empty;

        var customerSearchEntry = GeoraePlanTheme.CreateCompactEntry("거래처명 입력");
        customerSearchEntry.SetBinding(Entry.TextProperty, nameof(InvoiceDraftViewModel.CustomerSearchText));

        var customerSearchButton = GeoraePlanTheme.CreateCompactButton("찾기", GeoraePlanTheme.SecondaryButton);
        customerSearchButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await _viewModel.SearchCustomersAsync(),
                "전표 작성 작업");

        var customerSearchGrid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(92))
            }
        };
        customerSearchGrid.Add(customerSearchEntry);
        customerSearchGrid.Add(customerSearchButton, 1, 0);

        var customerResultView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("검색 결과 없음", true, 12),
            ItemTemplate = new DataTemplate(() =>
            {
                var nameLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
                nameLabel.FontAttributes = FontAttributes.Bold;
                nameLabel.LineHeight = 1.0;
                nameLabel.SetBinding(Label.TextProperty, nameof(CustomerDto.NameOriginal));

                var infoLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                infoLabel.LineHeight = 1.0;
                infoLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new CustomerInfoConverter()));

                var selectButton = GeoraePlanTheme.CreateCompactButton("선택", GeoraePlanTheme.Success);
                selectButton.Clicked += (sender, _) =>
                    MobileErrorHandler.FireAndForget(
                        async () =>
                        {
                    if (sender is Button button && button.BindingContext is CustomerDto customer)
                        await _viewModel.SelectCustomerAsync(customer);
                },
                        "전표 작성 작업");

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.Surface,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = new Thickness(10, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { nameLabel, infoLabel, selectButton }
                    }
                };
            })
        };
        customerResultView.SetBinding(ItemsView.ItemsSourceProperty, nameof(InvoiceDraftViewModel.CustomerSearchResults));
        customerResultView.SetBinding(VisualElement.HeightRequestProperty, nameof(InvoiceDraftViewModel.CustomerSearchResultsHeight));

        var selectedCustomerLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
        selectedCustomerLabel.LineHeight = 1.0;
        selectedCustomerLabel.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedCustomerSummary));

        var invoiceOfficePicker = GeoraePlanTheme.CreateCompactPicker("전표 소속 선택");
        invoiceOfficePicker.ItemDisplayBinding = new Binding(nameof(MobileOfficeOption.DisplayName));
        invoiceOfficePicker.SetBinding(Picker.ItemsSourceProperty, nameof(InvoiceDraftViewModel.InvoiceOfficeOptions));
        invoiceOfficePicker.SetBinding(Picker.SelectedItemProperty, nameof(InvoiceDraftViewModel.SelectedInvoiceOffice));
        invoiceOfficePicker.SetBinding(VisualElement.IsEnabledProperty, nameof(InvoiceDraftViewModel.CanChooseInvoiceOffice));

        var invoiceOfficeSummary = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        invoiceOfficeSummary.LineHeight = 1.0;
        invoiceOfficeSummary.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedInvoiceOfficeSummary));

        var categoryIntro = GeoraePlanTheme.CreateBodyText("품목분류를 먼저 선택하세요.", true, 12);
        categoryIntro.LineHeight = 1.0;
        categoryIntro.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.IsCategoryChooserVisible));

        _categoryButtonGrid = new Grid
        {
            ColumnSpacing = 10,
            RowSpacing = 10,
            IsVisible = true
        };
        _categoryButtonGrid.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.IsCategoryChooserVisible));

        var selectedCategoryLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
        selectedCategoryLabel.FontAttributes = FontAttributes.Bold;
        selectedCategoryLabel.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedCategoryHeader));

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
        selectedCategoryHeader.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.HasSelectedCategory));
        selectedCategoryHeader.Add(selectedCategoryLabel);
        selectedCategoryHeader.Add(changeCategoryButton, 1, 0);

        var categorySummary = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        categorySummary.LineHeight = 1.0;
        categorySummary.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedCategorySummary));
        categorySummary.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.HasSelectedCategory));

        var itemSearchEntry = GeoraePlanTheme.CreateCompactEntry("품목명 / 규격 검색");
        itemSearchEntry.SetBinding(Entry.TextProperty, nameof(InvoiceDraftViewModel.ItemSearchText));
        itemSearchEntry.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.HasSelectedCategory));
        itemSearchEntry.Completed += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await _viewModel.SearchItemsAsync(),
                "전표 작성 작업");

        var itemSearchButton = GeoraePlanTheme.CreateCompactButton("찾기", GeoraePlanTheme.SecondaryButton);
        itemSearchButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await _viewModel.SearchItemsAsync(),
                "전표 작성 작업");
        itemSearchButton.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.HasSelectedCategory));

        var itemSearchGrid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(92))
            }
        };
        itemSearchGrid.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.HasSelectedCategory));
        itemSearchGrid.Add(itemSearchEntry);
        itemSearchGrid.Add(itemSearchButton, 1, 0);

        var recentHeader = GeoraePlanTheme.CreateFieldLabel("최근 선택 품목");
        recentHeader.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.HasVisibleRecentItems));

        _recentItemsLayout = new FlexLayout
        {
            Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap,
            Direction = Microsoft.Maui.Layouts.FlexDirection.Row,
            JustifyContent = Microsoft.Maui.Layouts.FlexJustify.Start,
            AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start
        };

        var recentScroll = new ScrollView
        {
            Orientation = ScrollOrientation.Horizontal,
            Content = _recentItemsLayout
        };
        recentScroll.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.HasVisibleRecentItems));

        var itemListCaption = GeoraePlanTheme.CreateBodyText("현재 분류 품목", true, 11);
        itemListCaption.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedCategorySummary));
        itemListCaption.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.HasSelectedCategory));

        var itemResultView = new CollectionView
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

                var hintLabel = GeoraePlanTheme.CreateBodyText("탭하여 수량/단가 입력", true, 11);
                hintLabel.LineHeight = 1.0;

                var content = new VerticalStackLayout
                {
                    Spacing = 4,
                    Children = { nameLabel, specLabel, metaLabel, hintLabel }
                };

                var border = new Border
                {
                    BackgroundColor = GeoraePlanTheme.Surface,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = new Thickness(10, 9),
                    Margin = new Thickness(0, 0, 0, 6),
                    Content = content
                };

                var tap = new TapGestureRecognizer();
                tap.Tapped += (sender, _) =>
                    MobileErrorHandler.FireAndForget(
                        async () =>
                        {
                    if (sender is Border card && card.BindingContext is ItemDto item)
                        await _viewModel.SelectItemAsync(item);
                },
                        "전표 작성 작업");
                border.GestureRecognizers.Add(tap);
                return border;
            })
        };
        itemResultView.SetBinding(ItemsView.ItemsSourceProperty, nameof(InvoiceDraftViewModel.ItemSearchResults));
        itemResultView.SetBinding(VisualElement.HeightRequestProperty, nameof(InvoiceDraftViewModel.ItemSearchResultsHeight));
        itemResultView.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.HasSelectedCategory));

        var draftSummary = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 11);
        draftSummary.LineHeight = 1.0;
        draftSummary.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.DraftSummary));

        var lineList = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("추가된 품목 없음", true, 12),
            ItemTemplate = new DataTemplate(() =>
            {
                var titleLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
                titleLabel.FontAttributes = FontAttributes.Bold;
                titleLabel.LineHeight = 1.0;
                titleLabel.SetBinding(Label.TextProperty, nameof(InvoiceLineDraftItem.ItemNameOriginal));

                var specLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                specLabel.LineHeight = 1.0;
                specLabel.SetBinding(Label.TextProperty, new Binding(nameof(InvoiceLineDraftItem.SpecificationOriginal), stringFormat: "규격 {0}"));

                var qtyPriceLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                qtyPriceLabel.LineHeight = 1.0;
                qtyPriceLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new InvoiceLineSummaryConverter()));

                var summaryLabel = GeoraePlanTheme.CreateBodyText(string.Empty, false, 11);
                summaryLabel.LineHeight = 1.0;
                summaryLabel.SetBinding(Label.TextProperty, new Binding(nameof(InvoiceLineDraftItem.LineAmount), stringFormat: "합계 {0:N0}원"));

                var editButton = GeoraePlanTheme.CreateCompactButton("수정", GeoraePlanTheme.SecondaryButton);
                editButton.Clicked += (sender, _) =>
                    MobileErrorHandler.FireAndForget(
                        async () =>
                        {
                    if (sender is Button button && button.BindingContext is InvoiceLineDraftItem line)
                        await _viewModel.EditLineAsync(line);
                },
                        "전표 작성 작업");

                var deleteButton = GeoraePlanTheme.CreateCompactButton("삭제", GeoraePlanTheme.Danger);
                deleteButton.Clicked += (sender, _) =>
                    MobileErrorHandler.FireAndForget(
                        async () =>
                        {
                    if (sender is Button button && button.BindingContext is InvoiceLineDraftItem line)
                        await _viewModel.RemoveLineAsync(line);
                },
                        "전표 작성 작업");

                var actionGrid = new Grid
                {
                    ColumnSpacing = 8,
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Star)
                    }
                };
                actionGrid.Add(editButton);
                actionGrid.Add(deleteButton, 1, 0);

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.Surface,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = new Thickness(10, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { titleLabel, specLabel, qtyPriceLabel, summaryLabel, actionGrid }
                    }
                };
            })
        };
        lineList.SetBinding(ItemsView.ItemsSourceProperty, nameof(InvoiceDraftViewModel.LineItems));
        lineList.SetBinding(VisualElement.HeightRequestProperty, nameof(InvoiceDraftViewModel.LineItemsHeight));

        var dateLabel = GeoraePlanTheme.CreateFieldLabel("전표일자");
        var datePicker = new DatePicker
        {
            BackgroundColor = GeoraePlanTheme.InputBackground,
            TextColor = Colors.Black,
            HeightRequest = 36,
            Margin = Thickness.Zero
        };
        datePicker.SetBinding(DatePicker.DateProperty, nameof(InvoiceDraftViewModel.InvoiceDate));

        var memoLabel = GeoraePlanTheme.CreateFieldLabel("전표 메모");
        var memoEditor = GeoraePlanTheme.CreateCompactEditor("전표 메모", 64);
        memoEditor.SetBinding(Editor.TextProperty, nameof(InvoiceDraftViewModel.Memo));

        var saveButton = GeoraePlanTheme.CreateCompactButton("전표 임시저장", GeoraePlanTheme.Accent);
        saveButton.SetBinding(Button.CommandProperty, nameof(InvoiceDraftViewModel.SaveDraftCommand));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.StatusMessage));

        var activity = new ActivityIndicator { Color = GeoraePlanTheme.Accent, HeightRequest = 18 };
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(InvoiceDraftViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(InvoiceDraftViewModel.IsBusy));

        var mainContent = new VerticalStackLayout
        {
            Padding = 12,
            Spacing = 10,
            Children =
            {
                GeoraePlanTheme.CreateCompactCard(
                    GeoraePlanTheme.CreateSectionTitle("1단계 · 거래처 찾기", 15),
                    customerSearchGrid,
                    customerResultView,
                    selectedCustomerLabel,
                    GeoraePlanTheme.CreateFieldLabel("전표 소속"),
                    invoiceOfficePicker,
                    invoiceOfficeSummary),
                GeoraePlanTheme.CreateCompactCard(
                    GeoraePlanTheme.CreateSectionTitle("2단계 · 품목 선택", 15),
                    categoryIntro,
                    _categoryButtonGrid,
                    selectedCategoryHeader,
                    categorySummary,
                    itemSearchGrid,
                    recentHeader,
                    recentScroll,
                    itemListCaption,
                    itemResultView),
                GeoraePlanTheme.CreateCompactCard(
                    GeoraePlanTheme.CreateSectionTitle("추가된 품목", 15),
                    draftSummary,
                    lineList),
                GeoraePlanTheme.CreateCompactCard(
                    GeoraePlanTheme.CreateSectionTitle("전표 저장", 15),
                    dateLabel,
                    datePicker,
                    memoLabel,
                    memoEditor,
                    saveButton,
                    activity,
                    statusLabel)
            }
        };

        var scroll = new ScrollView { Content = mainContent };

        var sheetTitle = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 15);
        sheetTitle.FontAttributes = FontAttributes.Bold;
        sheetTitle.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedItemSheetTitle));

        var sheetSpec = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        sheetSpec.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedItemSheetSpecification));

        var sheetPrice = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        sheetPrice.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedItemPriceSummary));

        var sheetStock = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        sheetStock.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedItemStockSummary));

        var sheetMemo = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        sheetMemo.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedItemMemo));

        var branchStockLabel = GeoraePlanTheme.CreateFieldLabel("지점별 재고");
        var branchStockView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("재고 없음", true, 11),
            ItemTemplate = new DataTemplate(() =>
            {
                var warehouseLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 11);
                warehouseLabel.LineHeight = 1.0;
                warehouseLabel.SetBinding(Label.TextProperty, new Binding(nameof(ItemWarehouseStockDto.WarehouseCode), converter: new WarehouseDisplayNameConverter()));

                var quantityLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                quantityLabel.LineHeight = 1.0;
                quantityLabel.HorizontalTextAlignment = TextAlignment.End;
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

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.Surface,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 8 },
                    Padding = new Thickness(8, 5),
                    Margin = new Thickness(0, 0, 0, 4),
                    Content = rowGrid
                };
            })
        };
        branchStockView.SetBinding(ItemsView.ItemsSourceProperty, nameof(InvoiceDraftViewModel.SelectedItemBranchStocks));
        branchStockView.SetBinding(VisualElement.HeightRequestProperty, nameof(InvoiceDraftViewModel.SelectedItemBranchStocksHeight));

        var quantityLabel = GeoraePlanTheme.CreateFieldLabel("수량");
        var quantityEntry = GeoraePlanTheme.CreateCompactEntry("수량");
        quantityEntry.Keyboard = Keyboard.Numeric;
        quantityEntry.SetBinding(Entry.TextProperty, nameof(InvoiceDraftViewModel.LineQuantityText));

        var unitPriceLabel = GeoraePlanTheme.CreateFieldLabel("단가");
        var unitPriceEntry = GeoraePlanTheme.CreateCompactEntry("단가");
        unitPriceEntry.Keyboard = Keyboard.Numeric;
        unitPriceEntry.SetBinding(Entry.TextProperty, nameof(InvoiceDraftViewModel.LineUnitPriceText));

        var memoSheetLabel = GeoraePlanTheme.CreateFieldLabel("메모");
        var remarkEntry = GeoraePlanTheme.CreateCompactEntry("메모");
        remarkEntry.SetBinding(Entry.TextProperty, nameof(InvoiceDraftViewModel.LineRemark));

        var numericGrid = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            }
        };
        numericGrid.Add(new VerticalStackLayout { Spacing = 4, Children = { quantityLabel, quantityEntry } });
        numericGrid.Add(new VerticalStackLayout { Spacing = 4, Children = { unitPriceLabel, unitPriceEntry } }, 1, 0);

        var actionGrid = new Grid
        {
            ColumnSpacing = 10,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            }
        };

        var addLineButton = GeoraePlanTheme.CreateCompactButton("품목 추가", GeoraePlanTheme.Accent);
        addLineButton.SetBinding(Button.TextProperty, nameof(InvoiceDraftViewModel.LineActionText));
        addLineButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await _viewModel.AddOrUpdateLineAsync(),
                "전표 작성 작업");

        var cancelButton = GeoraePlanTheme.CreateCompactButton("취소", GeoraePlanTheme.SecondaryButton);
        cancelButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await _viewModel.CancelItemEntryAsync(),
                "전표 작성 작업");

        actionGrid.Add(addLineButton);
        actionGrid.Add(cancelButton, 1, 0);

        var bottomSheet = new Border
        {
            BackgroundColor = GeoraePlanTheme.SurfaceAlt,
            Stroke = GeoraePlanTheme.Border,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(20, 20, 0, 0) },
            Padding = new Thickness(16, 14, 16, 18),
            Margin = new Thickness(12, 0, 12, 12),
            VerticalOptions = LayoutOptions.End,
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
                    branchStockLabel,
                    branchStockView,
                    numericGrid,
                    new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { memoSheetLabel, remarkEntry }
                    },
                    actionGrid
                }
            }
        };
        bottomSheet.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.IsItemEntrySheetVisible));

        var backdrop = new Grid
        {
            BackgroundColor = Color.FromArgb("#70000000")
        };
        backdrop.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoiceDraftViewModel.IsItemEntrySheetVisible));
        var backdropTap = new TapGestureRecognizer();
        backdropTap.Tapped += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await _viewModel.CancelItemEntryAsync(),
                "전표 작성 작업");
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

        await MobileErrorHandler.RunGuardedAsync(
            async () =>
            {
try
        {
            await _viewModel.LoadAsync();
        }
        catch (MobileAuthenticationException ex)
        {
            _viewModel.StatusMessage = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"전표작성 화면 초기화 실패: {ex.Message}";
            return;
        }

        if (_preferredCustomerId.HasValue)
        {
            try
            {
                await _viewModel.PreselectCustomerAsync(_preferredCustomerId.Value, _preferredCustomerName);
            }
            catch (MobileAuthenticationException ex)
            {
                _viewModel.StatusMessage = ex.Message;
            }
            catch (Exception ex)
            {
                _viewModel.StatusMessage = $"선택 거래처 기본값 적용 실패: {ex.Message}";
            }
        }

        RebuildCategoryButtons();
        RebuildRecentItems();
            },
            "전표 작성 화면 초기화");
    }

    protected override void OnDisappearing()
    {
        _viewModel.SavedSuccessfully -= HandleSavedSuccessfullyAsync;
        _viewModel.PropertyChanged -= HandleViewModelPropertyChanged;
        _viewModel.ItemCategories.CollectionChanged -= HandleCategoryCollectionChanged;
        _viewModel.VisibleRecentItems.CollectionChanged -= HandleRecentCollectionChanged;
        base.OnDisappearing();
    }

    private async Task HandleSavedSuccessfullyAsync()
    {
        if (Navigation.NavigationStack.Count > 1)
            await Shell.Current.Navigation.PopAsync();
    }

    private void HandleCategoryCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildCategoryButtons();

    private void HandleRecentCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RebuildRecentItems();

    private void HandleViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InvoiceDraftViewModel.SelectedCategory) or nameof(InvoiceDraftViewModel.HasSelectedCategory))
        {
            RebuildCategoryButtons();
            RebuildRecentItems();
        }
    }

    protected override bool OnBackButtonPressed()
    {
        if (_viewModel.TryNavigateBackOneStep())
            return true;

        return base.OnBackButtonPressed();
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
            var isSelected = _viewModel.SelectedCategory is not null && string.Equals(_viewModel.SelectedCategory.Name, category.Name, StringComparison.OrdinalIgnoreCase);
            var button = GeoraePlanTheme.CreateButton(string.IsNullOrWhiteSpace(category.Name) ? "미분류" : category.Name, isSelected ? GeoraePlanTheme.Accent : GeoraePlanTheme.SecondaryButton);
            button.HeightRequest = 48;
            button.CornerRadius = 12;
            button.Padding = new Thickness(10, 4);
            button.Clicked += (_, _) =>
                MobileErrorHandler.FireAndForget(
                    async () => await _viewModel.SelectCategoryAsync(category),
                    "전표 작성 작업");
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
            button.Clicked += (_, _) =>
                MobileErrorHandler.FireAndForget(
                    async () => await _viewModel.SelectRecentItemAsync(recent),
                    "전표 작성 작업");
            _recentItemsLayout.Children.Add(button);
        }
    }

    private sealed class WarehouseDisplayNameConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => WarehouseDisplayNameResolver.Resolve(value?.ToString());

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class InvoiceLineSummaryConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not InvoiceLineDraftItem line)
                return string.Empty;

            return $"수량 {line.Quantity:N0} / 단가 {line.UnitPrice:N0}원";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class CustomerInfoConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not CustomerDto customer)
                return string.Empty;

            var phone = string.IsNullOrWhiteSpace(customer.Phone) ? "전화 미등록" : customer.Phone;
            var biz = string.IsNullOrWhiteSpace(customer.BusinessNumber) ? "사업자번호 미등록" : customer.BusinessNumber;
            return $"{phone} · {biz}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
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
}
