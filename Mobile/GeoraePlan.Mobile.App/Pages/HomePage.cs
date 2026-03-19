using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class HomePage : ContentPage
{
    private readonly HomeViewModel _viewModel;

    public HomePage()
    {
        Title = "홈";
        _viewModel = ServiceHelper.GetRequiredService<HomeViewModel>();
        BindingContext = _viewModel;

        var displayName = new Label { FontSize = 24, FontAttributes = FontAttributes.Bold };
        displayName.SetBinding(Label.TextProperty, nameof(HomeViewModel.DisplayName));

        var roleLabel = new Label();
        roleLabel.SetBinding(Label.TextProperty, nameof(HomeViewModel.RoleText));

        var syncLabel = new Label();
        syncLabel.SetBinding(Label.TextProperty, nameof(HomeViewModel.LastSyncText));

        var statusLabel = new Label { TextColor = Colors.DimGray };
        statusLabel.SetBinding(Label.TextProperty, nameof(HomeViewModel.StatusMessage));

        var refreshButton = new Button { Text = "상태 새로고침" };
        refreshButton.SetBinding(Button.CommandProperty, nameof(HomeViewModel.RefreshCommand));

        var createInvoiceButton = new Button { Text = "전표 작성" };
        createInvoiceButton.Clicked += async (_, _) =>
            await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<InvoiceDraftPage>());

        var createPaymentButton = new Button { Text = "수금 입력" };
        createPaymentButton.Clicked += async (_, _) =>
            await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<PaymentDraftPage>());

        var quickActionGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 12
        };
        quickActionGrid.Add(createInvoiceButton);
        Grid.SetColumn(createInvoiceButton, 0);
        quickActionGrid.Add(createPaymentButton);
        Grid.SetColumn(createPaymentButton, 1);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 16,
                Children =
                {
                    displayName,
                    roleLabel,
                    syncLabel,
                    new Border
                    {
                        Stroke = Colors.LightGray,
                        StrokeShape = new RoundRectangle { CornerRadius = 12 },
                        Padding = 16,
                        Content = new VerticalStackLayout
                        {
                            Spacing = 8,
                            Children =
                            {
                                new Label { Text = "빠른 안내", FontAttributes = FontAttributes.Bold },
                                new Label { Text = "거래처/품목/전표 탭은 NAS 서버 조회용 기본 화면입니다." },
                                new Label { Text = "동기화 탭은 안드로이드 스캐폴드용 수동 sync 상태를 보여줍니다." }
                            }
                        }
                    },
                    quickActionGrid,
                    refreshButton,
                    statusLabel
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.RefreshAsync();
    }
}
