using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class CustomersPage : ContentPage
{
    private readonly CustomersViewModel _viewModel;
    private readonly MobileRefreshCoordinator _refreshCoordinator;
    private readonly SyncCoordinator _syncCoordinator;
    private int _seenCustomersVersion;

    public CustomersPage()
    {
        GeoraePlanTheme.ApplyPage(this, "???");

        _viewModel = ServiceHelper.GetRequiredService<CustomersViewModel>();
        _refreshCoordinator = ServiceHelper.GetRequiredService<MobileRefreshCoordinator>();
        _syncCoordinator = ServiceHelper.GetRequiredService<SyncCoordinator>();
        BindingContext = _viewModel;

        var searchBar = GeoraePlanTheme.CreateSearchBar("???? / ?? / ?????");
        searchBar.HeightRequest = 42;
        searchBar.SetBinding(SearchBar.TextProperty, nameof(CustomersViewModel.SearchText));
        searchBar.SearchButtonPressed += async (_, _) => await _viewModel.RefreshAsync();
        searchBar.TextChanged += async (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.OldTextValue) && string.IsNullOrWhiteSpace(args.NewTextValue))
                await _viewModel.RefreshAsync();
        };

        var clearSearchButton = GeoraePlanTheme.CreateCompactButton("???", GeoraePlanTheme.SecondaryButton);
        clearSearchButton.SetBinding(VisualElement.IsVisibleProperty, nameof(CustomersViewModel.HasSearchText));
        clearSearchButton.Clicked += async (_, _) =>
        {
            _viewModel.ClearSearch();
            await _viewModel.RefreshAsync();
        };

        var refreshButton = GeoraePlanTheme.CreateCompactButton("議고쉶", GeoraePlanTheme.SecondaryButton);
        refreshButton.SetBinding(Button.CommandProperty, nameof(CustomersViewModel.RefreshCommand));

        var searchGrid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(86)),
                new ColumnDefinition(new GridLength(86))
            }
        };
        searchGrid.Add(searchBar);
        searchGrid.Add(clearSearchButton, 1, 0);
        searchGrid.Add(refreshButton, 2, 0);

        var activity = new ActivityIndicator { Color = GeoraePlanTheme.Accent, HeightRequest = 18 };
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(CustomersViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(CustomersViewModel.IsBusy));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(CustomersViewModel.StatusMessage));

        var detailName = GeoraePlanTheme.CreateSectionTitle(string.Empty, 14);
        detailName.SetBinding(Label.TextProperty, nameof(CustomersViewModel.SelectedCustomerName));

        var detailCounts = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        detailCounts.LineHeight = 1.0;
        detailCounts.SetBinding(Label.TextProperty, nameof(CustomersViewModel.SelectedCustomerSummaryCounts));

        var detailStatus = GeoraePlanTheme.CreateStatusLabel();
        detailStatus.SetBinding(Label.TextProperty, nameof(CustomersViewModel.DetailStatusMessage));

        var detailHeaderTitle = GeoraePlanTheme.CreateSectionTitle("?좏깮 嫄곕옒泥??곸꽭", 15);
        var closeDetailButton = GeoraePlanTheme.CreateCompactButton("?リ린", GeoraePlanTheme.SecondaryButton);
        closeDetailButton.Clicked += (_, _) => _viewModel.ClearSelectedCustomer();

        var detailHeader = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            }
        };
        detailHeader.Add(detailHeaderTitle);
        detailHeader.Add(closeDetailButton, 1, 0);

        var detailActivity = new ActivityIndicator { Color = GeoraePlanTheme.Accent, HeightRequest = 18 };
        detailActivity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(CustomersViewModel.IsDetailBusy));
        detailActivity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(CustomersViewModel.IsDetailBusy));

        var summaryPhoneLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
        summaryPhoneLabel.LineHeight = 1.0;
        summaryPhoneLabel.SetBinding(Label.TextProperty, nameof(CustomersViewModel.SelectedCustomerPhone), stringFormat: "??쒖쟾??{0}");

        var summaryMemoTitle = GeoraePlanTheme.CreateFieldLabel("硫붾え?ы빆");
        var summaryMemoLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        summaryMemoLabel.LineHeight = 1.0;
        summaryMemoLabel.SetBinding(Label.TextProperty, nameof(CustomersViewModel.SelectedCustomerNotes));

        var summarySection = new VerticalStackLayout
        {
            Spacing = 4,
            Children = { summaryPhoneLabel, summaryMemoTitle, summaryMemoLabel }
        };
        summarySection.SetBinding(VisualElement.IsVisibleProperty, nameof(CustomersViewModel.ShowSummarySection));

        var contractsView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("?깅줉??怨꾩빟?쒓? ?놁뒿?덈떎.", true, 11),
            ItemTemplate = new DataTemplate(() =>
            {
                var typeLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
                typeLabel.FontAttributes = FontAttributes.Bold;
                typeLabel.LineHeight = 1.0;
                typeLabel.SetBinding(Label.TextProperty, nameof(CustomerContractDto.ContractType));

                var dateLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                dateLabel.LineHeight = 1.0;
                dateLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new ContractDateSummaryConverter()));

                var openButton = GeoraePlanTheme.CreateCompactButton("PDF ?닿린", GeoraePlanTheme.Purple);
                openButton.Clicked += async (sender, _) =>
                {
                    if (sender is Button button && button.BindingContext is CustomerContractDto contract)
                        await _viewModel.OpenContractAsync(contract);
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
                        Children = { typeLabel, dateLabel, openButton }
                    }
                };
            })
        };
        contractsView.SetBinding(ItemsView.ItemsSourceProperty, nameof(CustomersViewModel.SelectedCustomerContracts));
        contractsView.SetBinding(VisualElement.HeightRequestProperty, nameof(CustomersViewModel.ContractsSectionHeight));

        var contractsSection = new VerticalStackLayout { Spacing = 4, Children = { contractsView } };
        contractsSection.SetBinding(VisualElement.IsVisibleProperty, nameof(CustomersViewModel.ShowContractsSection));

        var invoicesView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("理쒓렐 嫄곕옒?댁뿭???놁뒿?덈떎.", true, 11),
            ItemTemplate = new DataTemplate(() =>
            {
                var numberLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
                numberLabel.FontAttributes = FontAttributes.Bold;
                numberLabel.LineHeight = 1.0;
                numberLabel.SetBinding(Label.TextProperty, new Binding(nameof(InvoiceDto.InvoiceNumber), stringFormat: "?꾪몴 {0}"));

                var dateAmountLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                dateAmountLabel.LineHeight = 1.0;
                dateAmountLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new InvoiceSummaryConverter()));

                var memoLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                memoLabel.LineHeight = 1.0;
                memoLabel.LineBreakMode = LineBreakMode.TailTruncation;
                memoLabel.MaxLines = 1;
                memoLabel.SetBinding(Label.TextProperty, nameof(InvoiceDto.Memo));

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.Surface,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = new Thickness(10, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 3,
                        Children = { numberLabel, dateAmountLabel, memoLabel }
                    }
                };
            })
        };
        invoicesView.SetBinding(ItemsView.ItemsSourceProperty, nameof(CustomersViewModel.SelectedCustomerInvoices));
        invoicesView.SetBinding(VisualElement.HeightRequestProperty, nameof(CustomersViewModel.InvoicesSectionHeight));

        var invoicesSection = new VerticalStackLayout { Spacing = 4, Children = { invoicesView } };
        invoicesSection.SetBinding(VisualElement.IsVisibleProperty, nameof(CustomersViewModel.ShowInvoicesSection));

        var paymentsView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("理쒓렐 ?섍툑 ?댁뿭???놁뒿?덈떎.", true, 11),
            ItemTemplate = new DataTemplate(() =>
            {
                var invoiceLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
                invoiceLabel.FontAttributes = FontAttributes.Bold;
                invoiceLabel.LineHeight = 1.0;
                invoiceLabel.SetBinding(Label.TextProperty, nameof(CustomerPaymentHistoryRow.InvoiceDisplay), stringFormat: "?꾪몴 {0}");

                var dateAmountLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                dateAmountLabel.LineHeight = 1.0;
                dateAmountLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new PaymentSummaryConverter()));

                var noteLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                noteLabel.LineHeight = 1.0;
                noteLabel.LineBreakMode = LineBreakMode.TailTruncation;
                noteLabel.MaxLines = 1;
                noteLabel.SetBinding(Label.TextProperty, nameof(CustomerPaymentHistoryRow.NoteDisplay));

                var attachmentButton = GeoraePlanTheme.CreateCompactButton("泥⑤? 蹂닿린", GeoraePlanTheme.Purple);
                attachmentButton.SetBinding(VisualElement.IsVisibleProperty, nameof(CustomerPaymentHistoryRow.HasAttachments));
                attachmentButton.Clicked += async (sender, _) =>
                {
                    if (sender is not Button button || button.BindingContext is not CustomerPaymentHistoryRow row)
                        return;

                    await Shell.Current.Navigation.PushAsync(new PaymentAttachmentsPage(row.PaymentId, $"{row.InvoiceDisplay} ?섍툑 泥⑤?"));
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
                        Children = { invoiceLabel, dateAmountLabel, noteLabel, attachmentButton }
                    }
                };
            })
        };
        paymentsView.SetBinding(ItemsView.ItemsSourceProperty, nameof(CustomersViewModel.SelectedCustomerPayments));
        paymentsView.SetBinding(VisualElement.HeightRequestProperty, nameof(CustomersViewModel.PaymentsSectionHeight));

        var paymentsSection = new VerticalStackLayout { Spacing = 4, Children = { paymentsView } };
        paymentsSection.SetBinding(VisualElement.IsVisibleProperty, nameof(CustomersViewModel.ShowPaymentsSection));

        var summaryTabButton = CreateDetailTabButton("湲곕낯");
        var contractsTabButton = CreateDetailTabButton("怨꾩빟");
        var invoicesTabButton = CreateDetailTabButton("嫄곕옒?댁뿭");
        var paymentsTabButton = CreateDetailTabButton("?섍툑");

        void RefreshDetailTabButtons()
        {
            summaryTabButton.BackgroundColor = _viewModel.ShowSummarySection ? GeoraePlanTheme.Accent : GeoraePlanTheme.SecondaryButton;
            contractsTabButton.BackgroundColor = _viewModel.ShowContractsSection ? GeoraePlanTheme.Accent : GeoraePlanTheme.SecondaryButton;
            invoicesTabButton.BackgroundColor = _viewModel.ShowInvoicesSection ? GeoraePlanTheme.Accent : GeoraePlanTheme.SecondaryButton;
            paymentsTabButton.BackgroundColor = _viewModel.ShowPaymentsSection ? GeoraePlanTheme.Accent : GeoraePlanTheme.SecondaryButton;
        }

        summaryTabButton.Clicked += (_, _) => { _viewModel.ShowSummaryTab(); RefreshDetailTabButtons(); };
        contractsTabButton.Clicked += (_, _) => { _viewModel.ShowContractsTab(); RefreshDetailTabButtons(); };
        invoicesTabButton.Clicked += (_, _) => { _viewModel.ShowInvoicesTab(); RefreshDetailTabButtons(); };
        paymentsTabButton.Clicked += (_, _) => { _viewModel.ShowPaymentsTab(); RefreshDetailTabButtons(); };
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(CustomersViewModel.SelectedDetailSection) or nameof(CustomersViewModel.HasSelectedCustomer))
                RefreshDetailTabButtons();
        };
        RefreshDetailTabButtons();

        var tabGrid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            }
        };
        tabGrid.Add(summaryTabButton);
        tabGrid.Add(contractsTabButton, 1, 0);
        tabGrid.Add(invoicesTabButton, 2, 0);
        tabGrid.Add(paymentsTabButton, 3, 0);

        var detailCard = GeoraePlanTheme.CreateCompactCard(
            detailHeader,
            detailName,
            detailCounts,
            tabGrid,
            summarySection,
            contractsSection,
            invoicesSection,
            paymentsSection,
            detailActivity,
            detailStatus);
        detailCard.SetBinding(VisualElement.IsVisibleProperty, nameof(CustomersViewModel.HasSelectedCustomer));

        var collectionView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            ItemTemplate = new DataTemplate(() =>
            {
                var nameLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 15);
                nameLabel.FontAttributes = FontAttributes.Bold;
                nameLabel.LineHeight = 1.0;
                nameLabel.SetBinding(Label.TextProperty, nameof(CustomerDto.NameOriginal));

                var infoLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                infoLabel.LineHeight = 1.0;
                infoLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new CustomerInfoConverter()));

                var noteLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                noteLabel.LineHeight = 1.0;
                noteLabel.LineBreakMode = LineBreakMode.TailTruncation;
                noteLabel.MaxLines = 1;
                noteLabel.SetBinding(Label.TextProperty, nameof(CustomerDto.Notes));

                var invoiceButton = GeoraePlanTheme.CreateCompactButton("?꾪몴?묒꽦", GeoraePlanTheme.Success);
                invoiceButton.HorizontalOptions = LayoutOptions.Fill;
                invoiceButton.Clicked += async (sender, _) =>
                {
                    if (sender is not Button button || button.BindingContext is not CustomerDto customer)
                        return;

                    try
                    {
                        await Shell.Current.Navigation.PushAsync(new InvoiceDraftPage(customer.Id, customer.NameOriginal));
                    }
                    catch (Exception ex)
                    {
                        await DisplayAlert("?꾪몴?묒꽦 ?ㅻ쪟", $"?꾪몴?묒꽦 ?붾㈃???댁? 紐삵뻽?듬땲??\n{ex.Message}", "?뺤씤");
                    }
                };

                var contractButton = GeoraePlanTheme.CreateCompactButton("嫄곕옒泥?怨꾩빟??蹂닿린", GeoraePlanTheme.Purple);
                contractButton.HorizontalOptions = LayoutOptions.Fill;
                contractButton.Clicked += async (sender, _) =>
                {
                    if (sender is not Button button || button.BindingContext is not CustomerDto customer)
                        return;

                    await Shell.Current.Navigation.PushAsync(new CustomerContractsPage(customer.Id, customer.NameOriginal));
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
                actionGrid.Add(invoiceButton);
                actionGrid.Add(contractButton, 1, 0);

                var body = new VerticalStackLayout
                {
                    Spacing = 4,
                    Children = { nameLabel, infoLabel, noteLabel, actionGrid }
                };

                var border = new Border
                {
                    BackgroundColor = GeoraePlanTheme.SurfaceAlt,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Padding = new Thickness(10, 8),
                    Margin = new Thickness(0, 0, 0, 5),
                    Content = body
                };

                var tap = new TapGestureRecognizer();
                tap.Tapped += async (_, _) =>
                {
                    if (border.BindingContext is CustomerDto customer)
                        await _viewModel.SelectCustomerAsync(customer);
                };
                border.GestureRecognizers.Add(tap);

                return border;
            })
        };
        collectionView.SetBinding(ItemsView.ItemsSourceProperty, nameof(CustomersViewModel.Customers));

        var contentGrid = new Grid
        {
            Padding = 12,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            },
            RowSpacing = 10
        };
        contentGrid.Add(searchGrid);
        Grid.SetRow(searchGrid, 0);
        contentGrid.Add(activity);
        Grid.SetRow(activity, 1);
        contentGrid.Add(statusLabel);
        Grid.SetRow(statusLabel, 2);
        contentGrid.Add(detailCard);
        Grid.SetRow(detailCard, 3);
        contentGrid.Add(collectionView);
        Grid.SetRow(collectionView, 4);

        Content = contentGrid;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            await _syncCoordinator.RefreshIfServerChangedAsync("customers-page", TimeSpan.FromSeconds(5));

            var versionChanged = _seenCustomersVersion != _refreshCoordinator.CustomersVersion;
            if (versionChanged || _viewModel.NeedsRefresh(TimeSpan.FromSeconds(30)))
                await _viewModel.RefreshAsync();

            _seenCustomersVersion = _refreshCoordinator.CustomersVersion;
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"嫄곕옒泥??붾㈃ 珥덇린???ㅽ뙣: {ex.Message}";
        }
    }

    private static Button CreateDetailTabButton(string text)
    {
        var button = GeoraePlanTheme.CreateCompactButton(text, GeoraePlanTheme.SecondaryButton);
        button.FontSize = 12;
        return button;
    }

    private sealed class ContractDateSummaryConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not CustomerContractDto contract)
                return string.Empty;

            var signed = contract.SignedDate.HasValue ? contract.SignedDate.Value.ToString("yyyy-MM-dd") : "???";
            var expire = contract.ExpireDate.HasValue ? contract.ExpireDate.Value.ToString("yyyy-MM-dd") : "???";
            return $"泥닿껐 {signed} / 留뚮즺 {expire}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class InvoiceSummaryConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not InvoiceDto invoice)
                return string.Empty;

            return $"{invoice.InvoiceDate:yyyy-MM-dd} ? {invoice.TotalAmount:N0}?";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class PaymentSummaryConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not CustomerPaymentHistoryRow row)
                return string.Empty;

            return $"{row.PaymentDate:yyyy-MM-dd} 쨌 {row.AmountDisplay}";
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

            var biz = string.IsNullOrWhiteSpace(customer.BusinessNumber) ? "????? ???" : customer.BusinessNumber;
            var phone = string.IsNullOrWhiteSpace(customer.Phone) ? "?? ???" : customer.Phone;
            return $"{biz} 쨌 {phone}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}
