using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class ItemsPage : ContentPage
{
    private readonly ItemsViewModel _viewModel;

    public ItemsPage()
    {
        Title = "품목";
        _viewModel = ServiceHelper.GetRequiredService<ItemsViewModel>();
        BindingContext = _viewModel;

        var searchBar = new SearchBar { Placeholder = "품목명 / 규격" };
        searchBar.SetBinding(SearchBar.TextProperty, nameof(ItemsViewModel.SearchText));

        var refreshButton = new Button { Text = "조회" };
        refreshButton.SetBinding(Button.CommandProperty, nameof(ItemsViewModel.RefreshCommand));

        var statusLabel = new Label { TextColor = Colors.DimGray };
        statusLabel.SetBinding(Label.TextProperty, nameof(ItemsViewModel.StatusMessage));

        var collectionView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            ItemTemplate = new DataTemplate(() =>
            {
                var nameLabel = new Label { FontAttributes = FontAttributes.Bold, FontSize = 16 };
                nameLabel.SetBinding(Label.TextProperty, nameof(ItemDto.NameOriginal));

                var specLabel = new Label();
                specLabel.SetBinding(Label.TextProperty, nameof(ItemDto.SpecificationOriginal));

                var unitLabel = new Label { TextColor = Colors.DimGray, FontSize = 12 };
                unitLabel.SetBinding(Label.TextProperty, nameof(ItemDto.Unit));

                return new Border
                {
                    Stroke = Colors.LightGray,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = 12,
                    Margin = new Thickness(0, 0, 0, 8),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { nameLabel, specLabel, unitLabel }
                    }
                };
            })
        };
        collectionView.SetBinding(ItemsView.ItemsSourceProperty, nameof(ItemsViewModel.Items));

        Content = new VerticalStackLayout
        {
            Padding = 16,
            Spacing = 12,
            Children =
            {
                searchBar,
                refreshButton,
                statusLabel,
                collectionView
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_viewModel.Items.Count == 0)
            await _viewModel.RefreshAsync();
    }
}
