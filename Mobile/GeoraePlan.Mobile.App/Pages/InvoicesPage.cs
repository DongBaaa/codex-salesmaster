using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class InvoicesPage : ContentPage
{
    private readonly InvoicesViewModel _viewModel;

    public InvoicesPage()
    {
        Title = "전표";
        _viewModel = ServiceHelper.GetRequiredService<InvoicesViewModel>();
        BindingContext = _viewModel;

        var searchBar = new SearchBar { Placeholder = "전표번호 / 메모" };
        searchBar.SetBinding(SearchBar.TextProperty, nameof(InvoicesViewModel.SearchText));

        var refreshButton = new Button { Text = "조회" };
        refreshButton.SetBinding(Button.CommandProperty, nameof(InvoicesViewModel.RefreshCommand));

        var createInvoiceButton = new Button { Text = "전표 작성" };
        createInvoiceButton.Clicked += async (_, _) =>
            await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<InvoiceDraftPage>());

        var createPaymentButton = new Button { Text = "수금 입력" };
        createPaymentButton.Clicked += async (_, _) =>
            await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<PaymentDraftPage>());

        var statusLabel = new Label { TextColor = Colors.DimGray };
        statusLabel.SetBinding(Label.TextProperty, nameof(InvoicesViewModel.StatusMessage));

        var collectionView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() =>
            {
                var numberLabel = new Label { FontAttributes = FontAttributes.Bold, FontSize = 16 };
                numberLabel.SetBinding(Label.TextProperty, nameof(InvoiceDto.InvoiceNumber));

                var dateLabel = new Label();
                dateLabel.SetBinding(Label.TextProperty, new Binding(nameof(InvoiceDto.InvoiceDate), stringFormat: "{0:yyyy-MM-dd}"));

                var amountLabel = new Label { TextColor = Colors.DimGray, FontSize = 12 };
                amountLabel.SetBinding(Label.TextProperty, new Binding(nameof(InvoiceDto.TotalAmount), stringFormat: "{0:N0}원"));

                return new Border
                {
                    Stroke = Colors.LightGray,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = 12,
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

        Content = new VerticalStackLayout
        {
            Padding = 16,
            Spacing = 12,
            Children =
            {
                searchBar,
                actionGrid,
                statusLabel,
                collectionView
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_viewModel.Invoices.Count == 0)
            await _viewModel.RefreshAsync();
    }
}
