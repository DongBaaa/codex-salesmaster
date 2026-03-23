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
        GeoraePlanTheme.ApplyPage(this, "거래처");

        _viewModel = ServiceHelper.GetRequiredService<CustomersViewModel>();
        _refreshCoordinator = ServiceHelper.GetRequiredService<MobileRefreshCoordinator>();
        _syncCoordinator = ServiceHelper.GetRequiredService<SyncCoordinator>();
        BindingContext = _viewModel;

        var searchBar = GeoraePlanTheme.CreateSearchBar("거래처명 / 전화 / 사업자번호");
        searchBar.HeightRequest = 42;
        searchBar.SetBinding(SearchBar.TextProperty, nameof(CustomersViewModel.SearchText));
        searchBar.SearchButtonPressed += async (_, _) => await _viewModel.RefreshAsync();
        searchBar.TextChanged += async (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.OldTextValue) && string.IsNullOrWhiteSpace(args.NewTextValue))
                await _viewModel.RefreshAsync();
        };

        var clearSearchButton = GeoraePlanTheme.CreateCompactButton("초기화", GeoraePlanTheme.SecondaryButton);
        clearSearchButton.WidthRequest = 78;
        clearSearchButton.SetBinding(VisualElement.IsVisibleProperty, nameof(CustomersViewModel.HasSearchText));
        clearSearchButton.Clicked += async (_, _) =>
        {
            _viewModel.ClearSearch();
            await _viewModel.RefreshAsync();
        };

        var refreshButton = GeoraePlanTheme.CreateCompactButton("조회", GeoraePlanTheme.SecondaryButton);
        refreshButton.WidthRequest = 86;
        refreshButton.SetBinding(Button.CommandProperty, nameof(CustomersViewModel.RefreshCommand));

        var searchActions = new HorizontalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.End,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                clearSearchButton,
                refreshButton
            }
        };

        var searchGrid = new Grid
        {
            HorizontalOptions = LayoutOptions.Fill,
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            }
        };
        searchBar.HorizontalOptions = LayoutOptions.Fill;
        searchGrid.Add(searchBar);
        searchGrid.Add(searchActions, 1, 0);

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

        var detailHeaderTitle = GeoraePlanTheme.CreateSectionTitle("선택 거래처 상세", 15);
        var closeDetailButton = GeoraePlanTheme.CreateCompactButton("닫기", GeoraePlanTheme.SecondaryButton);
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
        summaryPhoneLabel.SetBinding(Label.TextProperty, nameof(CustomersViewModel.SelectedCustomerPhone), stringFormat: "대표전화 {0}");

        var summaryMemoTitle = GeoraePlanTheme.CreateFieldLabel("메모사항");
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
            EmptyView = GeoraePlanTheme.CreateBodyText("등록된 계약서가 없습니다.", true, 11),
            ItemTemplate = new DataTemplate(() =>
            {
                var typeLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
                typeLabel.FontAttributes = FontAttributes.Bold;
                typeLabel.LineHeight = 1.0;
                typeLabel.SetBinding(Label.TextProperty, nameof(CustomerContractDto.ContractType));

                var dateLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                dateLabel.LineHeight = 1.0;
                dateLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new ContractDateSummaryConverter()));

                var openButton = GeoraePlanTheme.CreateCompactButton("PDF 열기", GeoraePlanTheme.Purple);
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
            EmptyView = GeoraePlanTheme.CreateBodyText("최근 거래내역이 없습니다.", true, 11),
            ItemTemplate = new DataTemplate(() =>
            {
                var numberLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
                numberLabel.FontAttributes = FontAttributes.Bold;
                numberLabel.LineHeight = 1.0;
                numberLabel.SetBinding(Label.TextProperty, new Binding(nameof(InvoiceDto.InvoiceNumber), stringFormat: "전표 {0}"));

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
            EmptyView = GeoraePlanTheme.CreateBodyText("최근 수금 내역이 없습니다.", true, 11),
            ItemTemplate = new DataTemplate(() =>
            {
                var invoiceLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
                invoiceLabel.FontAttributes = FontAttributes.Bold;
                invoiceLabel.LineHeight = 1.0;
                invoiceLabel.SetBinding(Label.TextProperty, nameof(CustomerPaymentHistoryRow.InvoiceDisplay), stringFormat: "전표 {0}");

                var dateAmountLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                dateAmountLabel.LineHeight = 1.0;
                dateAmountLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new PaymentSummaryConverter()));

                var noteLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                noteLabel.LineHeight = 1.0;
                noteLabel.LineBreakMode = LineBreakMode.TailTruncation;
                noteLabel.MaxLines = 1;
                noteLabel.SetBinding(Label.TextProperty, nameof(CustomerPaymentHistoryRow.NoteDisplay));

                var attachmentButton = GeoraePlanTheme.CreateCompactButton("첨부 보기", GeoraePlanTheme.Purple);
                attachmentButton.SetBinding(VisualElement.IsVisibleProperty, nameof(CustomerPaymentHistoryRow.HasAttachments));
                attachmentButton.Clicked += async (sender, _) =>
                {
                    if (sender is not Button button || button.BindingContext is not CustomerPaymentHistoryRow row)
                        return;

                    await Shell.Current.Navigation.PushAsync(new PaymentAttachmentsPage(row.PaymentId, $"{row.InvoiceDisplay} 수금 첨부"));
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

        var summaryTabButton = CreateDetailTabButton("기본");
        var contractsTabButton = CreateDetailTabButton("계약");
        var invoicesTabButton = CreateDetailTabButton("거래내역");
        var paymentsTabButton = CreateDetailTabButton("수금");

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

                var invoiceButton = GeoraePlanTheme.CreateCompactButton("전표작성", GeoraePlanTheme.Success);
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
                        await DisplayAlert("전표작성 오류", $"전표작성 화면을 열지 못했습니다.\n{ex.Message}", "확인");
                    }
                };

                var contractButton = GeoraePlanTheme.CreateCompactButton("거래처 계약서 보기", GeoraePlanTheme.Purple);
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
            _viewModel.StatusMessage = $"거래처 화면 초기화 실패: {ex.Message}";
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

            var signed = contract.SignedDate.HasValue ? contract.SignedDate.Value.ToString("yyyy-MM-dd") : "미정";
            var expire = contract.ExpireDate.HasValue ? contract.ExpireDate.Value.ToString("yyyy-MM-dd") : "미정";
            return $"체결 {signed} / 만료 {expire}";
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

            return $"{invoice.InvoiceDate:yyyy-MM-dd} · {invoice.TotalAmount:N0}원";
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

            return $"{row.PaymentDate:yyyy-MM-dd} · {row.AmountDisplay}";
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

            var biz = string.IsNullOrWhiteSpace(customer.BusinessNumber) ? "사업자번호 없음" : customer.BusinessNumber;
            var phone = string.IsNullOrWhiteSpace(customer.Phone) ? "전화 없음" : customer.Phone;
            return $"{biz} · {phone}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}
