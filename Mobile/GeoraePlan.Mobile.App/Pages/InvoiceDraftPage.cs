using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class InvoiceDraftPage : ContentPage
{
    private readonly InvoiceDraftViewModel _viewModel;
    private readonly Guid? _preferredCustomerId;
    private readonly string _preferredCustomerName;

    public InvoiceDraftPage(Guid? preferredCustomerId = null, string? preferredCustomerName = null)
    {
        GeoraePlanTheme.ApplyPage(this, "전표 작성");

        _viewModel = ServiceHelper.GetRequiredService<InvoiceDraftViewModel>();
        _viewModel.SavedSuccessfully += HandleSavedSuccessfullyAsync;
        BindingContext = _viewModel;
        _preferredCustomerId = preferredCustomerId;
        _preferredCustomerName = preferredCustomerName?.Trim() ?? string.Empty;

        var customerSearchEntry = GeoraePlanTheme.CreateCompactEntry("거래처명 입력");
        customerSearchEntry.SetBinding(Entry.TextProperty, nameof(InvoiceDraftViewModel.CustomerSearchText));

        var customerSearchButton = GeoraePlanTheme.CreateCompactButton("찾기", GeoraePlanTheme.SecondaryButton);
        customerSearchButton.Clicked += async (_, _) => await _viewModel.SearchCustomersAsync();

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
                selectButton.Clicked += async (sender, _) =>
                {
                    if (sender is Button button && button.BindingContext is CustomerDto customer)
                        await _viewModel.SelectCustomerAsync(customer);
                };

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

        var categoryPicker = GeoraePlanTheme.CreateCompactPicker("품목분류 선택");
        categoryPicker.SetBinding(Picker.ItemsSourceProperty, nameof(InvoiceDraftViewModel.ItemCategories));
        categoryPicker.SetBinding(Picker.SelectedItemProperty, nameof(InvoiceDraftViewModel.SelectedCategory));
        categoryPicker.ItemDisplayBinding = new Binding(nameof(ItemCategorySummaryDto.Name));

        var categorySummary = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        categorySummary.LineHeight = 1.0;
        categorySummary.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedCategorySummary));

        var itemSearchEntry = GeoraePlanTheme.CreateCompactEntry("품목명 / 규격 입력");
        itemSearchEntry.SetBinding(Entry.TextProperty, nameof(InvoiceDraftViewModel.ItemSearchText));

        var itemSearchButton = GeoraePlanTheme.CreateCompactButton("찾기", GeoraePlanTheme.SecondaryButton);
        itemSearchButton.Clicked += async (_, _) => await _viewModel.SearchItemsAsync();

        var itemSearchGrid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(92))
            }
        };
        itemSearchGrid.Add(itemSearchEntry);
        itemSearchGrid.Add(itemSearchButton, 1, 0);

        var itemResultView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("품목 검색 결과 없음", true, 12),
            ItemTemplate = new DataTemplate(() =>
            {
                var nameLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
                nameLabel.FontAttributes = FontAttributes.Bold;
                nameLabel.LineHeight = 1.0;
                nameLabel.SetBinding(Label.TextProperty, nameof(ItemDto.NameOriginal));

                var specLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                specLabel.LineHeight = 1.0;
                specLabel.SetBinding(Label.TextProperty, nameof(ItemDto.SpecificationOriginal));

                var stockLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                stockLabel.LineHeight = 1.0;
                stockLabel.SetBinding(Label.TextProperty, new Binding(nameof(ItemDto.CurrentStock), stringFormat: "현재재고 {0:N0}"));

                var selectButton = GeoraePlanTheme.CreateCompactButton("품목 선택", GeoraePlanTheme.Purple);
                selectButton.Clicked += async (sender, _) =>
                {
                    if (sender is Button button && button.BindingContext is ItemDto item)
                        await _viewModel.SelectItemAsync(item);
                };

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
                        Children = { nameLabel, specLabel, stockLabel, selectButton }
                    }
                };
            })
        };
        itemResultView.SetBinding(ItemsView.ItemsSourceProperty, nameof(InvoiceDraftViewModel.ItemSearchResults));
        itemResultView.SetBinding(VisualElement.HeightRequestProperty, nameof(InvoiceDraftViewModel.ItemSearchResultsHeight));

        var selectedItemSummary = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
        selectedItemSummary.LineHeight = 1.0;
        selectedItemSummary.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedItemSummary));

        var selectedItemPriceSummary = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        selectedItemPriceSummary.LineHeight = 1.0;
        selectedItemPriceSummary.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedItemPriceSummary));

        var selectedItemStockSummary = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        selectedItemStockSummary.LineHeight = 1.0;
        selectedItemStockSummary.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedItemStockSummary));

        var selectedItemMemo = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        selectedItemMemo.LineHeight = 1.0;
        selectedItemMemo.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.SelectedItemMemo));

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

        var quantityField = new VerticalStackLayout
        {
            Spacing = 4,
            Children = { quantityLabel, quantityEntry }
        };

        var unitPriceField = new VerticalStackLayout
        {
            Spacing = 4,
            Children = { unitPriceLabel, unitPriceEntry }
        };

        var numericGrid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            }
        };
        numericGrid.Add(quantityField);
        numericGrid.Add(unitPriceField, 1, 0);

        var lineRemarkLabel = GeoraePlanTheme.CreateFieldLabel("품목 메모");
        var lineRemarkEntry = GeoraePlanTheme.CreateCompactEntry("품목 메모");
        lineRemarkEntry.SetBinding(Entry.TextProperty, nameof(InvoiceDraftViewModel.LineRemark));

        var remarkField = new VerticalStackLayout
        {
            Spacing = 4,
            Children = { lineRemarkLabel, lineRemarkEntry }
        };

        var addLineButton = GeoraePlanTheme.CreateCompactButton("품목 추가", GeoraePlanTheme.Accent);
        addLineButton.SetBinding(Button.TextProperty, nameof(InvoiceDraftViewModel.LineActionText));
        addLineButton.Clicked += async (_, _) => await _viewModel.AddOrUpdateLineAsync();

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
                editButton.Clicked += async (sender, _) =>
                {
                    if (sender is Button button && button.BindingContext is InvoiceLineDraftItem line)
                        await _viewModel.EditLineAsync(line);
                };

                var deleteButton = GeoraePlanTheme.CreateCompactButton("삭제", GeoraePlanTheme.Danger);
                deleteButton.Clicked += async (sender, _) =>
                {
                    if (sender is Button button && button.BindingContext is InvoiceLineDraftItem line)
                        await _viewModel.RemoveLineAsync(line);
                };

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

        var draftSummary = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 11);
        draftSummary.LineHeight = 1.0;
        draftSummary.SetBinding(Label.TextProperty, nameof(InvoiceDraftViewModel.DraftSummary));

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

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
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
                        GeoraePlanTheme.CreateSectionTitle("2단계 · 품목분류 / 품목검색", 15),
                        categoryPicker,
                        categorySummary,
                        itemSearchGrid,
                        itemResultView),
                    GeoraePlanTheme.CreateCompactCard(
                        GeoraePlanTheme.CreateSectionTitle("3단계 · 품목 상세", 15),
                        selectedItemSummary,
                        selectedItemPriceSummary,
                        selectedItemStockSummary,
                        selectedItemMemo,
                        GeoraePlanTheme.CreateFieldLabel("지점별 재고"),
                        branchStockView,
                        numericGrid,
                        remarkField,
                        addLineButton),
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
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();

        if (_preferredCustomerId.HasValue)
            await _viewModel.PreselectCustomerAsync(_preferredCustomerId.Value, _preferredCustomerName);
    }

    protected override void OnDisappearing()
    {
        _viewModel.SavedSuccessfully -= HandleSavedSuccessfullyAsync;
        base.OnDisappearing();
    }

    private async Task HandleSavedSuccessfullyAsync()
    {
        if (Navigation.NavigationStack.Count > 1)
            await Shell.Current.Navigation.PopAsync();
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
}
