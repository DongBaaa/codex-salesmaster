using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class RecycleBinPage : ContentPage
{
    private readonly RecycleBinViewModel _viewModel;

    public RecycleBinPage()
    {
        GeoraePlanTheme.ApplyPage(this, "휴지통");

        _viewModel = ServiceHelper.GetRequiredService<RecycleBinViewModel>();
        BindingContext = _viewModel;

        var searchBar = GeoraePlanTheme.CreateSearchBar("제목 / 보조정보 / 상세 검색");
        searchBar.SetBinding(SearchBar.TextProperty, nameof(RecycleBinViewModel.SearchText));

        var kindPicker = GeoraePlanTheme.CreatePicker("구분 선택");
        kindPicker.ItemsSource = _viewModel.KindOptions;
        kindPicker.ItemDisplayBinding = new Binding(nameof(RecycleBinFilterOption.DisplayName));
        kindPicker.SelectedIndex = 0;
        kindPicker.SelectedIndexChanged += (_, _) =>
        {
            if (kindPicker.SelectedItem is RecycleBinFilterOption option)
                _viewModel.SelectedKind = option.Value;
        };

        var refreshButton = GeoraePlanTheme.CreateButton("휴지통 조회", GeoraePlanTheme.SecondaryButton);
        refreshButton.SetBinding(Button.CommandProperty, nameof(RecycleBinViewModel.RefreshCommand));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(RecycleBinViewModel.StatusMessage));

        var collectionView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            ItemTemplate = new DataTemplate(() =>
            {
                var kindLabel = new Label { TextColor = GeoraePlanTheme.Accent, FontAttributes = FontAttributes.Bold };
                kindLabel.SetBinding(Label.TextProperty, nameof(RecycleBinEntryDto.KindText));

                var titleLabel = new Label { FontAttributes = FontAttributes.Bold, FontSize = 16, TextColor = GeoraePlanTheme.TextPrimary };
                titleLabel.SetBinding(Label.TextProperty, nameof(RecycleBinEntryDto.Title));

                var subtitleLabel = new Label { TextColor = GeoraePlanTheme.TextSecondary };
                subtitleLabel.SetBinding(Label.TextProperty, nameof(RecycleBinEntryDto.Subtitle));

                var detailLabel = new Label { FontSize = 12, TextColor = GeoraePlanTheme.TextSecondary };
                detailLabel.SetBinding(Label.TextProperty, nameof(RecycleBinEntryDto.Detail));

                var deletedLabel = new Label { FontSize = 11, TextColor = GeoraePlanTheme.TextSecondary };
                deletedLabel.SetBinding(Label.TextProperty, new Binding(nameof(RecycleBinEntryDto.DeletedAtUtc), stringFormat: "삭제시각 {0:yyyy-MM-dd HH:mm}"));

                var restoreButton = GeoraePlanTheme.CreateButton("복원", GeoraePlanTheme.Success);
                restoreButton.Clicked += (sender, _) =>
                    MobileErrorHandler.FireAndForget(
                        async () =>
                        {
                    if (sender is not Button button || button.BindingContext is not RecycleBinEntryDto entry)
                        return;

                    var confirm = await Application.Current!.MainPage!.DisplayAlert(
                        "휴지통 복원",
                        $"'{entry.Title}' 항목을 복원하시겠습니까?",
                        "복원",
                        "취소");
                    if (!confirm)
                        return;

                    await _viewModel.RestoreAsync(entry);
                },
                        "휴지통 작업");

                var purgeButton = GeoraePlanTheme.CreateButton("영구삭제", GeoraePlanTheme.Danger);
                purgeButton.Clicked += (sender, _) =>
                    MobileErrorHandler.FireAndForget(
                        async () =>
                        {
                    if (sender is not Button button || button.BindingContext is not RecycleBinEntryDto entry)
                        return;

                    var confirm = await Application.Current!.MainPage!.DisplayAlert(
                        "휴지통 영구삭제",
                        $"'{entry.Title}' 항목을 영구삭제하시겠습니까? 이 작업은 되돌릴 수 없습니다.",
                        "영구삭제",
                        "취소");
                    if (!confirm)
                        return;

                    await _viewModel.PurgeAsync(entry);
                },
                        "휴지통 작업");

                var actionGrid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Star)
                    },
                    ColumnSpacing = 8
                };
                actionGrid.Add(restoreButton);
                Grid.SetColumn(restoreButton, 0);
                actionGrid.Add(purgeButton);
                Grid.SetColumn(purgeButton, 1);

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.SurfaceAlt,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Padding = 14,
                    Margin = new Thickness(0, 0, 0, 8),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 6,
                        Children =
                        {
                            kindLabel,
                            titleLabel,
                            subtitleLabel,
                            detailLabel,
                            deletedLabel,
                            actionGrid
                        }
                    }
                };
            })
        };
        collectionView.SetBinding(ItemsView.ItemsSourceProperty, nameof(RecycleBinViewModel.Entries));

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
        contentGrid.Add(searchBar);
        Grid.SetRow(searchBar, 0);
        contentGrid.Add(kindPicker);
        Grid.SetRow(kindPicker, 1);
        contentGrid.Add(refreshButton);
        Grid.SetRow(refreshButton, 2);
        contentGrid.Add(statusLabel);
        Grid.SetRow(statusLabel, 3);
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
if (_viewModel.Entries.Count == 0)
            await _viewModel.RefreshAsync();
            },
            "휴지통 화면 초기화");
    }
}

