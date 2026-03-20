using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class CustomerContractsPage : ContentPage
{
    private readonly CustomerContractsViewModel _viewModel;
    private readonly Guid _customerId;
    private readonly string _customerName;
    private bool _initialized;

    public CustomerContractsPage(Guid customerId, string customerName)
    {
        GeoraePlanTheme.ApplyPage(this, "계약서");

        _customerId = customerId;
        _customerName = customerName;
        _viewModel = ServiceHelper.GetRequiredService<CustomerContractsViewModel>();
        BindingContext = _viewModel;

        var titleLabel = new Label
        {
            FontSize = 20,
            FontAttributes = FontAttributes.Bold,
            TextColor = GeoraePlanTheme.TextPrimary
        };
        titleLabel.SetBinding(Label.TextProperty, nameof(CustomerContractsViewModel.CustomerName));

        var refreshButton = GeoraePlanTheme.CreateButton("새로고침", GeoraePlanTheme.SecondaryButton);
        refreshButton.SetBinding(Button.CommandProperty, nameof(CustomerContractsViewModel.RefreshCommand));

        var activity = new ActivityIndicator { Color = GeoraePlanTheme.Accent };
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(CustomerContractsViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(CustomerContractsViewModel.IsBusy));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(CustomerContractsViewModel.StatusMessage));

        var collectionView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            ItemTemplate = new DataTemplate(() =>
            {
                var typeLabel = new Label { FontAttributes = FontAttributes.Bold, FontSize = 15, TextColor = GeoraePlanTheme.TextPrimary };
                typeLabel.SetBinding(Label.TextProperty, nameof(CustomerContractDto.ContractType));

                var fileLabel = new Label { FontSize = 13, TextColor = GeoraePlanTheme.TextSecondary };
                fileLabel.SetBinding(Label.TextProperty, nameof(CustomerContractDto.FileName));

                var expireLabel = new Label { FontSize = 12, TextColor = GeoraePlanTheme.TextSecondary };
                expireLabel.SetBinding(Label.TextProperty, new Binding(nameof(CustomerContractDto.ExpireDate), stringFormat: "만료일 {0:yyyy-MM-dd}"));

                var signedLabel = new Label { FontSize = 12, TextColor = GeoraePlanTheme.TextSecondary };
                signedLabel.SetBinding(Label.TextProperty, new Binding(nameof(CustomerContractDto.SignedDate), stringFormat: "체결일 {0:yyyy-MM-dd}"));

                var infoLabel = new Label { FontSize = 12, TextColor = GeoraePlanTheme.TextSecondary };
                infoLabel.SetBinding(Label.TextProperty, nameof(CustomerContractDto.Description));

                var primaryBadge = new Label
                {
                    Text = "대표 계약서",
                    TextColor = Colors.White,
                    BackgroundColor = GeoraePlanTheme.Accent,
                    Padding = new Thickness(8, 2),
                    FontSize = 11,
                    HorizontalOptions = LayoutOptions.Start
                };
                primaryBadge.SetBinding(IsVisibleProperty, nameof(CustomerContractDto.IsPrimary));

                var openButton = GeoraePlanTheme.CreateButton("PDF 열기", GeoraePlanTheme.Purple);
                openButton.HorizontalOptions = LayoutOptions.End;
                openButton.Clicked += async (sender, _) =>
                {
                    if (sender is Button button && button.BindingContext is CustomerContractDto contract)
                        await _viewModel.OpenContractAsync(contract);
                };

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
                            primaryBadge,
                            typeLabel,
                            fileLabel,
                            signedLabel,
                            expireLabel,
                            infoLabel,
                            openButton
                        }
                    }
                };
            })
        };
        collectionView.SetBinding(ItemsView.ItemsSourceProperty, nameof(CustomerContractsViewModel.Contracts));

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
        contentGrid.Add(titleLabel);
        Grid.SetRow(titleLabel, 0);
        contentGrid.Add(refreshButton);
        Grid.SetRow(refreshButton, 1);
        contentGrid.Add(activity);
        Grid.SetRow(activity, 2);
        contentGrid.Add(statusLabel);
        Grid.SetRow(statusLabel, 3);
        contentGrid.Add(collectionView);
        Grid.SetRow(collectionView, 4);

        Content = contentGrid;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        if (_initialized)
            return;

        _initialized = true;
        await _viewModel.InitializeAsync(_customerId, _customerName);
    }
}

