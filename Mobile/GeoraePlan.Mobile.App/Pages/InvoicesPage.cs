using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class InvoicesPage : ContentPage
{
    private readonly InvoicesViewModel _viewModel;
    private readonly MobileRefreshCoordinator _refreshCoordinator;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly SessionStore _sessionStore;
    private int _seenInvoicesVersion;

    public InvoicesPage()
    {
        GeoraePlanTheme.ApplyPage(this, "전표");

        _viewModel = ServiceHelper.GetRequiredService<InvoicesViewModel>();
        _refreshCoordinator = ServiceHelper.GetRequiredService<MobileRefreshCoordinator>();
        _syncCoordinator = ServiceHelper.GetRequiredService<SyncCoordinator>();
        _sessionStore = ServiceHelper.GetRequiredService<SessionStore>();
        _refreshCoordinator.AllChanged += HandleRealtimeRefreshRequested;
        BindingContext = _viewModel;

        var searchBar = GeoraePlanTheme.CreateSearchBar("거래처명 / 전표번호 / 메모");
        searchBar.SetBinding(SearchBar.TextProperty, nameof(InvoicesViewModel.SearchText));
        searchBar.SearchButtonPressed += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await _viewModel.RefreshAsync(),
                "전표 작업");
        searchBar.TextChanged += (_, args) =>
            MobileErrorHandler.FireAndForget(
                async () =>
                {
            if (!string.IsNullOrWhiteSpace(args.OldTextValue) && string.IsNullOrWhiteSpace(args.NewTextValue))
                await _viewModel.RefreshAsync();
        },
                "전표 작업");

        var clearSearchButton = GeoraePlanTheme.CreateCompactButton("초기화", GeoraePlanTheme.SecondaryButton);
        clearSearchButton.WidthRequest = 78;
        clearSearchButton.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoicesViewModel.HasSearchText));
        clearSearchButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () =>
                {
            _viewModel.ClearSearch();
            await _viewModel.RefreshAsync();
        },
                "전표 작업");

        var refreshButton = GeoraePlanTheme.CreateCompactButton("조회", GeoraePlanTheme.SecondaryButton);
        refreshButton.WidthRequest = 86;
        refreshButton.SetBinding(Button.CommandProperty, nameof(InvoicesViewModel.RefreshCommand));

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

        var canCreateInvoices = _sessionStore.GetSnapshot().CanCreateInvoices;
        var createSalesInvoiceButton = GeoraePlanTheme.CreateCompactButton("판매 작성", GeoraePlanTheme.Success);
        createSalesInvoiceButton.IsVisible = canCreateInvoices;
        createSalesInvoiceButton.IsEnabled = canCreateInvoices;
        createSalesInvoiceButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(new InvoiceDraftPage(VoucherType.Sales)),
                "전표 작업");

        var createPurchaseInvoiceButton = GeoraePlanTheme.CreateCompactButton("구매 작성", GeoraePlanTheme.Brown);
        createPurchaseInvoiceButton.IsVisible = canCreateInvoices;
        createPurchaseInvoiceButton.IsEnabled = canCreateInvoices;
        createPurchaseInvoiceButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(new InvoiceDraftPage(VoucherType.Purchase)),
                "전표 작업");

        var canCreatePayments = _sessionStore.GetSnapshot().CanCreatePayments;
        var createPaymentButton = GeoraePlanTheme.CreateCompactButton("수금/지급", GeoraePlanTheme.Purple);
        createPaymentButton.IsVisible = canCreatePayments;
        createPaymentButton.IsEnabled = canCreatePayments;
        createPaymentButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<PaymentDraftPage>()),
                "전표 작업");

        var actionGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 8
        };
        actionGrid.Add(createSalesInvoiceButton, 0, 0);
        actionGrid.Add(createPurchaseInvoiceButton, 1, 0);
        actionGrid.Add(createPaymentButton, 2, 0);

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(InvoicesViewModel.StatusMessage));

        var detailHeaderTitle = GeoraePlanTheme.CreateSectionTitle("선택 전표 상세", 15);
        var closeDetailButton = GeoraePlanTheme.CreateCompactButton("닫기", GeoraePlanTheme.SecondaryButton);
        closeDetailButton.Clicked += (_, _) => _viewModel.ClearSelectedInvoice();

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

        var detailCustomer = GeoraePlanTheme.CreateSectionTitle(string.Empty, 14);
        detailCustomer.SetBinding(Label.TextProperty, nameof(InvoicesViewModel.SelectedInvoiceCustomerName));

        var detailNumber = GeoraePlanTheme.CreateBodyText(string.Empty, false, 12);
        detailNumber.LineHeight = 1.0;
        detailNumber.SetBinding(Label.TextProperty, nameof(InvoicesViewModel.SelectedInvoiceNumberDisplay));

        var detailDate = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        detailDate.LineHeight = 1.0;
        detailDate.SetBinding(Label.TextProperty, nameof(InvoicesViewModel.SelectedInvoiceDateDisplay));

        var detailAmount = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        detailAmount.LineHeight = 1.0;
        detailAmount.SetBinding(Label.TextProperty, nameof(InvoicesViewModel.SelectedInvoiceAmountSummary));

        var detailMemoTitle = GeoraePlanTheme.CreateFieldLabel("메모");
        var detailMemo = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        detailMemo.LineHeight = 1.0;
        detailMemo.SetBinding(Label.TextProperty, nameof(InvoicesViewModel.SelectedInvoiceMemo));

        var linesLabel = GeoraePlanTheme.CreateFieldLabel("품목 목록");
        var linesView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("입력된 품목이 없습니다.", true, 11),
            ItemTemplate = new DataTemplate(() =>
            {
                var nameLabel = GeoraePlanTheme.CreateBodyText(string.Empty, false, 12);
                nameLabel.FontAttributes = FontAttributes.Bold;
                nameLabel.LineHeight = 1.0;
                nameLabel.SetBinding(Label.TextProperty, nameof(InvoiceLineDto.ItemNameOriginal));

                var specLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                specLabel.LineHeight = 1.0;
                specLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new InvoiceLineSummaryConverter()));

                var amountLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                amountLabel.LineHeight = 1.0;
                amountLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new InvoiceLineAmountConverter()));

                var remarkLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                remarkLabel.LineHeight = 1.0;
                remarkLabel.LineBreakMode = LineBreakMode.TailTruncation;
                remarkLabel.MaxLines = 1;
                remarkLabel.SetBinding(Label.TextProperty, nameof(InvoiceLineDto.Remark));

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
                        Children = { nameLabel, specLabel, amountLabel, remarkLabel }
                    }
                };
            })
        };
        linesView.SetBinding(ItemsView.ItemsSourceProperty, nameof(InvoicesViewModel.SelectedInvoiceLines));
        linesView.SetBinding(VisualElement.HeightRequestProperty, nameof(InvoicesViewModel.SelectedInvoiceLinesHeight));

        var paymentsLabel = GeoraePlanTheme.CreateFieldLabel("수금/지급 정보");
        var paymentSummary = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
        paymentSummary.LineHeight = 1.0;
        paymentSummary.SetBinding(Label.TextProperty, nameof(InvoicesViewModel.SelectedInvoicePaymentSummary));

        var paymentsView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("연결된 수금/지급 정보가 없습니다.", true, 11),
            ItemTemplate = new DataTemplate(() =>
            {
                var dateLabel = GeoraePlanTheme.CreateBodyText(string.Empty, false, 12);
                dateLabel.FontAttributes = FontAttributes.Bold;
                dateLabel.LineHeight = 1.0;
                dateLabel.SetBinding(Label.TextProperty, new Binding(nameof(PaymentDto.PaymentDate), stringFormat: "{0:yyyy-MM-dd}"));

                var amountLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                amountLabel.LineHeight = 1.0;
                amountLabel.SetBinding(Label.TextProperty, new Binding(nameof(PaymentDto.Amount), stringFormat: "{0:N0}원"));

                var noteLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                noteLabel.LineHeight = 1.0;
                noteLabel.LineBreakMode = LineBreakMode.TailTruncation;
                noteLabel.MaxLines = 1;
                noteLabel.SetBinding(Label.TextProperty, nameof(PaymentDto.Note));

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
                        Children = { dateLabel, amountLabel, noteLabel }
                    }
                };
            })
        };
        paymentsView.SetBinding(ItemsView.ItemsSourceProperty, nameof(InvoicesViewModel.SelectedInvoicePayments));
        paymentsView.SetBinding(VisualElement.HeightRequestProperty, nameof(InvoicesViewModel.SelectedInvoicePaymentsHeight));

        var selectedPaymentButton = GeoraePlanTheme.CreateCompactButton("수금/지급", GeoraePlanTheme.Purple);
        selectedPaymentButton.IsVisible = canCreatePayments;
        selectedPaymentButton.IsEnabled = canCreatePayments;
        selectedPaymentButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () =>
                {
                    if (_viewModel.SelectedInvoice is not null)
                        await Shell.Current.Navigation.PushAsync(new PaymentDraftPage(_viewModel.SelectedInvoice));
                },
                "전표 작업");

        var selectedEditButton = GeoraePlanTheme.CreateCompactButton("전표 수정", GeoraePlanTheme.Accent);
        selectedEditButton.IsVisible = canCreateInvoices;
        selectedEditButton.IsEnabled = canCreateInvoices;
        selectedEditButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () =>
                {
                    if (_viewModel.SelectedInvoice is not null)
                        await Shell.Current.Navigation.PushAsync(new InvoiceDraftPage(_viewModel.SelectedInvoice));
                },
                "전표 작업");

        var selectedDetailActionGrid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            }
        };
        selectedDetailActionGrid.Add(selectedPaymentButton, 0, 0);
        selectedDetailActionGrid.Add(selectedEditButton, 1, 0);

        var detailCard = GeoraePlanTheme.CreateCompactCard(
            detailHeader,
            detailCustomer,
            detailNumber,
            detailDate,
            detailAmount,
            detailMemoTitle,
            detailMemo,
            linesLabel,
            linesView,
            paymentsLabel,
            paymentSummary,
            paymentsView,
            selectedDetailActionGrid);
        detailCard.SetBinding(VisualElement.IsVisibleProperty, nameof(InvoicesViewModel.HasSelectedInvoice));

        var collectionView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            ItemTemplate = new DataTemplate(() =>
            {
                var customerLabel = GeoraePlanTheme.CreateBodyText(string.Empty, false, 15);
                customerLabel.FontAttributes = FontAttributes.Bold;
                customerLabel.LineHeight = 1.0;
                customerLabel.SetBinding(Label.TextProperty, nameof(InvoiceListItem.CustomerDisplayName));

                var numberLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
                numberLabel.LineHeight = 1.0;
                numberLabel.SetBinding(Label.TextProperty, nameof(InvoiceListItem.InvoiceNumberDisplay), stringFormat: "전표번호 {0}");

                var dateAmountLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                dateAmountLabel.LineHeight = 1.0;
                dateAmountLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new InvoiceRowSummaryConverter()));

                var paymentLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                paymentLabel.LineHeight = 1.0;
                paymentLabel.SetBinding(Label.TextProperty, nameof(InvoiceListItem.PaymentDisplay));

                var memoLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                memoLabel.LineHeight = 1.0;
                memoLabel.LineBreakMode = LineBreakMode.TailTruncation;
                memoLabel.MaxLines = 1;
                memoLabel.SetBinding(Label.TextProperty, nameof(InvoiceListItem.MemoDisplay));

                var border = new Border
                {
                    BackgroundColor = GeoraePlanTheme.SurfaceAlt,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Padding = new Thickness(12, 10),
                    Margin = new Thickness(0, 0, 0, 8),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { customerLabel, numberLabel, dateAmountLabel, paymentLabel, memoLabel }
                    }
                };

                var tap = new TapGestureRecognizer();
                tap.Tapped += (_, _) =>
                    MobileErrorHandler.FireAndForget(
                        async () =>
                        {
                    if (border.BindingContext is InvoiceListItem invoice)
                        await _viewModel.SelectInvoiceAsync(invoice);
                },
                        "전표 작업");
                border.GestureRecognizers.Add(tap);

                return border;
            })
        };
        collectionView.SetBinding(ItemsView.ItemsSourceProperty, nameof(InvoicesViewModel.Invoices));

        var contentGrid = new Grid
        {
            Padding = 16,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            },
            RowSpacing = 12
        };
        contentGrid.Add(searchGrid);
        Grid.SetRow(searchGrid, 0);
        contentGrid.Add(actionGrid);
        Grid.SetRow(actionGrid, 1);
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

        await MobileErrorHandler.RunGuardedAsync(
            async () =>
            {
try
        {
            await _syncCoordinator.RefreshIfServerChangedAsync("invoices-page", TimeSpan.FromSeconds(5));

            var versionChanged = _seenInvoicesVersion != _refreshCoordinator.InvoicesVersion;
            if (versionChanged || _viewModel.NeedsRefresh(TimeSpan.FromSeconds(15)))
                await _viewModel.RefreshAsync();

            _seenInvoicesVersion = _refreshCoordinator.InvoicesVersion;
        }
        catch (Exception ex)
        {
            _viewModel.StatusMessage = $"전표 화면 초기화 실패: {ex.Message}";
        }
            },
            "전표 화면 초기화");
    }

    private void HandleRealtimeRefreshRequested(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
            MobileErrorHandler.FireAndForget(
                async () =>
                {
                    if (Shell.Current?.CurrentPage == this)
                    {
                        await _viewModel.RefreshAsync();
                        _seenInvoicesVersion = _refreshCoordinator.InvoicesVersion;
                    }
                },
                "전표 실시간 갱신"));
    }

    protected override bool OnBackButtonPressed()
    {
        if (_viewModel.TryNavigateBackOneStep())
            return true;

        return base.OnBackButtonPressed();
    }

    private sealed class InvoiceRowSummaryConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not InvoiceListItem invoice)
                return string.Empty;

            return $"{invoice.DateDisplay} · 합계 {invoice.AmountDisplay}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class InvoiceLineSummaryConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not InvoiceLineDto line)
                return string.Empty;

            var spec = string.IsNullOrWhiteSpace(line.SpecificationOriginal) ? "규격 없음" : line.SpecificationOriginal;
            var unit = string.IsNullOrWhiteSpace(line.Unit) ? "EA" : line.Unit;
            return $"{spec} · 수량 {line.Quantity:N0} {unit}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class InvoiceLineAmountConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not InvoiceLineDto line)
                return string.Empty;

            return $"단가 {line.UnitPrice:N0}원 · 금액 {line.LineAmount:N0}원";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }
}
