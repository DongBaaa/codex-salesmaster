using GeoraePlan.Mobile.App.ViewModels;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class CustomersPage : ContentPage
{
    private readonly CustomersViewModel _viewModel;

    public CustomersPage()
    {
        Title = "거래처";
        _viewModel = ServiceHelper.GetRequiredService<CustomersViewModel>();
        BindingContext = _viewModel;

        var searchBar = new SearchBar { Placeholder = "거래처명 / 전화 / 사업자번호" };
        searchBar.SetBinding(SearchBar.TextProperty, nameof(CustomersViewModel.SearchText));

        var refreshButton = new Button { Text = "조회" };
        refreshButton.SetBinding(Button.CommandProperty, nameof(CustomersViewModel.RefreshCommand));

        var activity = new ActivityIndicator();
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(CustomersViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(CustomersViewModel.IsBusy));

        var statusLabel = new Label { TextColor = Colors.DimGray };
        statusLabel.SetBinding(Label.TextProperty, nameof(CustomersViewModel.StatusMessage));

        var collectionView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() =>
            {
                var nameLabel = new Label { FontAttributes = FontAttributes.Bold, FontSize = 16 };
                nameLabel.SetBinding(Label.TextProperty, nameof(CustomerDto.NameOriginal));

                var phoneLabel = new Label();
                phoneLabel.SetBinding(Label.TextProperty, nameof(CustomerDto.Phone));

                var bizLabel = new Label { TextColor = Colors.DimGray, FontSize = 12 };
                bizLabel.SetBinding(Label.TextProperty, nameof(CustomerDto.BusinessNumber));

                return new Border
                {
                    Stroke = Colors.LightGray,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = 12,
                    Margin = new Thickness(0, 0, 0, 8),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { nameLabel, phoneLabel, bizLabel }
                    }
                };
            })
        };
        collectionView.SetBinding(ItemsView.ItemsSourceProperty, nameof(CustomersViewModel.Customers));

        Content = new VerticalStackLayout
        {
            Padding = 16,
            Spacing = 12,
            Children =
            {
                searchBar,
                refreshButton,
                activity,
                statusLabel,
                collectionView
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_viewModel.Customers.Count == 0)
            await _viewModel.RefreshAsync();
    }
}
