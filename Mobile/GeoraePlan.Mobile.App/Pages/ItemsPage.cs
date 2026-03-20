using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class ItemsPage : ContentPage
{
    private readonly ItemsViewModel _viewModel;
    private readonly MobileRefreshCoordinator _refreshCoordinator;
    private readonly SyncCoordinator _syncCoordinator;
    private int _seenItemsVersion;

    public ItemsPage()
    {
        GeoraePlanTheme.ApplyPage(this, "품목");

        _viewModel = ServiceHelper.GetRequiredService<ItemsViewModel>();
        _refreshCoordinator = ServiceHelper.GetRequiredService<MobileRefreshCoordinator>();
        _syncCoordinator = ServiceHelper.GetRequiredService<SyncCoordinator>();
        BindingContext = _viewModel;

        var searchBar = GeoraePlanTheme.CreateSearchBar("품목명 / 규격");
        searchBar.SetBinding(SearchBar.TextProperty, nameof(ItemsViewModel.SearchText));

        var refreshButton = GeoraePlanTheme.CreateButton("조회", GeoraePlanTheme.SecondaryButton);
        refreshButton.SetBinding(Button.CommandProperty, nameof(ItemsViewModel.RefreshCommand));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(ItemsViewModel.StatusMessage));

        var collectionView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            ItemTemplate = new DataTemplate(() =>
            {
                var nameLabel = new Label { FontAttributes = FontAttributes.Bold, FontSize = 16, TextColor = GeoraePlanTheme.TextPrimary };
                nameLabel.SetBinding(Label.TextProperty, nameof(ItemDto.NameOriginal));

                var specLabel = new Label { TextColor = GeoraePlanTheme.TextSecondary };
                specLabel.SetBinding(Label.TextProperty, nameof(ItemDto.SpecificationOriginal));

                var unitLabel = new Label { TextColor = GeoraePlanTheme.TextSecondary, FontSize = 12 };
                unitLabel.SetBinding(Label.TextProperty, nameof(ItemDto.Unit));

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
                        Children = { nameLabel, specLabel, unitLabel }
                    }
                };
            })
        };
        collectionView.SetBinding(ItemsView.ItemsSourceProperty, nameof(ItemsViewModel.Items));

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
        contentGrid.Add(refreshButton);
        Grid.SetRow(refreshButton, 1);
        contentGrid.Add(statusLabel);
        Grid.SetRow(statusLabel, 2);
        contentGrid.Add(collectionView);
        Grid.SetRow(collectionView, 3);

        Content = contentGrid;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _syncCoordinator.TryBackgroundSyncAsync("items-page", TimeSpan.FromSeconds(45));

        var versionChanged = _seenItemsVersion != _refreshCoordinator.ItemsVersion;
        if (versionChanged || _viewModel.NeedsRefresh(TimeSpan.FromSeconds(30)))
            await _viewModel.RefreshAsync();

        _seenItemsVersion = _refreshCoordinator.ItemsVersion;
    }
}
