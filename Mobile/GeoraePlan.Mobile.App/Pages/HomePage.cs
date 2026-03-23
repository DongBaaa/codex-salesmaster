using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class HomePage : ContentPage
{
    private readonly HomeViewModel _viewModel;
    private readonly SyncCoordinator _syncCoordinator;

    public HomePage()
    {
        GeoraePlanTheme.ApplyPage(this, "홈");

        _viewModel = ServiceHelper.GetRequiredService<HomeViewModel>();
        _syncCoordinator = ServiceHelper.GetRequiredService<SyncCoordinator>();
        BindingContext = _viewModel;

        var displayName = new Label
        {
            FontSize = 24,
            FontAttributes = FontAttributes.Bold,
            TextColor = GeoraePlanTheme.TextPrimary
        };
        displayName.SetBinding(Label.TextProperty, nameof(HomeViewModel.DisplayName));

        var roleLabel = GeoraePlanTheme.CreateBodyText(string.Empty);
        roleLabel.SetBinding(Label.TextProperty, nameof(HomeViewModel.RoleText));

        var syncLabel = GeoraePlanTheme.CreateBodyText(string.Empty);
        syncLabel.SetBinding(Label.TextProperty, nameof(HomeViewModel.LastSyncText));

        var autoSyncLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        autoSyncLabel.SetBinding(Label.TextProperty, nameof(HomeViewModel.AutoSyncText));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(HomeViewModel.StatusMessage));

        var pendingNoticeLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
        pendingNoticeLabel.TextColor = Colors.White;
        pendingNoticeLabel.SetBinding(Label.TextProperty, nameof(HomeViewModel.PendingNoticeText));

        var pendingNoticeCard = new Border
        {
            BackgroundColor = Color.FromArgb("#3A2B12"),
            Stroke = GeoraePlanTheme.Brown,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Padding = 16,
            Content = new VerticalStackLayout
            {
                Spacing = 6,
                Children =
                {
                    GeoraePlanTheme.CreateSectionTitle("업로드 대기 알림", 15),
                    pendingNoticeLabel
                }
            }
        };
        pendingNoticeCard.SetBinding(IsVisibleProperty, nameof(HomeViewModel.HasPendingNotice));

        var refreshButton = GeoraePlanTheme.CreateButton("상태 새로고침", GeoraePlanTheme.SecondaryButton);
        refreshButton.SetBinding(Button.CommandProperty, nameof(HomeViewModel.RefreshCommand));

        var createInvoiceButton = GeoraePlanTheme.CreateButton("전표 작성", GeoraePlanTheme.Success);
        createInvoiceButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<InvoiceDraftPage>()),
                "빠른 메뉴 이동");

        var createPaymentButton = GeoraePlanTheme.CreateButton("수금 입력", GeoraePlanTheme.Purple);
        createPaymentButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<PaymentDraftPage>()),
                "빠른 메뉴 이동");

        var inventoryTransferButton = GeoraePlanTheme.CreateButton("재고이동", GeoraePlanTheme.Accent);
        inventoryTransferButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<InventoryTransfersPage>()),
                "빠른 메뉴 이동");

        var rentalsButton = GeoraePlanTheme.CreateButton("렌탈", GeoraePlanTheme.SecondaryButton);
        rentalsButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<RentalsPage>()),
                "빠른 메뉴 이동");

        var quickActionGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            },
            ColumnSpacing = 12,
            RowSpacing = 12
        };
        quickActionGrid.Add(createInvoiceButton);
        Grid.SetColumn(createInvoiceButton, 0);
        quickActionGrid.Add(createPaymentButton);
        Grid.SetColumn(createPaymentButton, 1);
        quickActionGrid.Add(inventoryTransferButton, 0, 1);
        quickActionGrid.Add(rentalsButton, 1, 1);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    GeoraePlanTheme.CreateCard(
                        displayName,
                        roleLabel,
                        syncLabel,
                        autoSyncLabel),
                    pendingNoticeCard,
                    GeoraePlanTheme.CreateCard(
                        GeoraePlanTheme.CreateSectionTitle("빠른 안내"),
                        GeoraePlanTheme.CreateBodyText("PC 버전과 같은 진한 네이비/블루 톤을 유지합니다."),
                        GeoraePlanTheme.CreateBodyText("거래처, 품목, 전표, 수금, 재고이동, 렌탈 화면은 같은 NAS 서버 sync 데이터를 기준으로 동작합니다."),
                        quickActionGrid,
                        refreshButton,
                        statusLabel)
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        await MobileErrorHandler.RunGuardedAsync(
            async () =>
            {
await _syncCoordinator.RefreshIfServerChangedAsync("home-page", TimeSpan.FromSeconds(5));
        await _viewModel.RefreshAsync();
            },
            "홈 화면 초기화");
    }
}
