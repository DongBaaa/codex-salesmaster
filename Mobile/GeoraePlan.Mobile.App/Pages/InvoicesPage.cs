using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class InvoicesPage : ContentPage
{
    private readonly InvoicesViewModel _viewModel;
    private readonly MobileRefreshCoordinator _refreshCoordinator;
    private readonly SyncCoordinator _syncCoordinator;
    private int _seenInvoicesVersion;

    public InvoicesPage()
    {
        GeoraePlanTheme.ApplyPage(this, "전표");

        _viewModel = ServiceHelper.GetRequiredService<InvoicesViewModel>();
        _refreshCoordinator = ServiceHelper.GetRequiredService<MobileRefreshCoordinator>();
        _syncCoordinator = ServiceHelper.GetRequiredService<SyncCoordinator>();
        BindingContext = _viewModel;

        var searchBar = GeoraePlanTheme.CreateSearchBar("전표번호 / 메모");
        searchBar.SetBinding(SearchBar.TextProperty, nameof(InvoicesViewModel.SearchText));

        var refreshButton = GeoraePlanTheme.CreateButton("조회", GeoraePlanTheme.SecondaryButton);
        refreshButton.SetBinding(Button.CommandProperty, nameof(InvoicesViewModel.RefreshCommand));

        var createInvoiceButton = GeoraePlanTheme.CreateButton("전표 작성", GeoraePlanTheme.Success);
        createInvoiceButton.Clicked += async (_, _) =>
            await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<InvoiceDraftPage>());

        var createPaymentButton = GeoraePlanTheme.CreateButton("수금 입력", GeoraePlanTheme.Purple);
        createPaymentButton.Clicked += async (_, _) =>
            await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<PaymentDraftPage>());

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(InvoicesViewModel.StatusMessage));

        var collectionView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            ItemTemplate = new DataTemplate(() =>
            {
                var numberLabel = new Label { FontAttributes = FontAttributes.Bold, FontSize = 16, TextColor = GeoraePlanTheme.TextPrimary };
                numberLabel.SetBinding(Label.TextProperty, nameof(InvoiceDto.InvoiceNumber));

                var dateLabel = new Label { TextColor = GeoraePlanTheme.TextSecondary };
                dateLabel.SetBinding(Label.TextProperty, new Binding(nameof(InvoiceDto.InvoiceDate), stringFormat: "{0:yyyy-MM-dd}"));

                var amountLabel = new Label { TextColor = GeoraePlanTheme.TextSecondary, FontSize = 12 };
                amountLabel.SetBinding(Label.TextProperty, new Binding(nameof(InvoiceDto.TotalAmount), stringFormat: "{0:N0}원"));

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.SurfaceAlt,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Padding = 14,
                    Margin = new Thickness(0, 0, 0, 8),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { numberLabel, dateLabel, amountLabel }
                    }
                };
            })
        };
        collectionView.SetBinding(ItemsView.ItemsSourceProperty, nameof(InvoicesViewModel.Invoices));

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
        actionGrid.Add(refreshButton);
        Grid.SetColumn(refreshButton, 0);
        actionGrid.Add(createInvoiceButton);
        Grid.SetColumn(createInvoiceButton, 1);
        actionGrid.Add(createPaymentButton);
        Grid.SetColumn(createPaymentButton, 2);

        var contentGrid = new Grid
        {
            Padding = 16,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            },
            RowSpacing = 12
        };
        contentGrid.Add(searchBar);
        Grid.SetRow(searchBar, 0);
        contentGrid.Add(actionGrid);
        Grid.SetRow(actionGrid, 1);
        contentGrid.Add(statusLabel);
        Grid.SetRow(statusLabel, 2);
        contentGrid.Add(collectionView);
        Grid.SetRow(collectionView, 3);

        Content = contentGrid;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _syncCoordinator.TryBackgroundSyncAsync("invoices-page", TimeSpan.FromSeconds(45));

        var versionChanged = _seenInvoicesVersion != _refreshCoordinator.InvoicesVersion;
        if (versionChanged || _viewModel.NeedsRefresh(TimeSpan.FromSeconds(15)))
            await _viewModel.RefreshAsync();

        _seenInvoicesVersion = _refreshCoordinator.InvoicesVersion;
    }
}
