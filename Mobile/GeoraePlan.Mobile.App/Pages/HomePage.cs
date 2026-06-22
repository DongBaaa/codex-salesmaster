using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class HomePage : ContentPage
{
    private readonly HomeViewModel _viewModel;
    private readonly SessionStore _sessionStore;
    private readonly SyncCoordinator _syncCoordinator;
    private readonly MobileRefreshCoordinator _refreshCoordinator;

    public HomePage()
    {
        GeoraePlanTheme.ApplyPage(this, "홈");

        _viewModel = ServiceHelper.GetRequiredService<HomeViewModel>();
        _sessionStore = ServiceHelper.GetRequiredService<SessionStore>();
        _syncCoordinator = ServiceHelper.GetRequiredService<SyncCoordinator>();
        _refreshCoordinator = ServiceHelper.GetRequiredService<MobileRefreshCoordinator>();
        _refreshCoordinator.AllChanged += HandleRealtimeRefreshRequested;
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

        var canCreateInvoices = _sessionStore.GetSnapshot().CanCreateInvoices;
        var createSalesInvoiceButton = GeoraePlanTheme.CreateButton("판매 작성", GeoraePlanTheme.Success);
        createSalesInvoiceButton.IsVisible = canCreateInvoices;
        createSalesInvoiceButton.IsEnabled = canCreateInvoices;
        createSalesInvoiceButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(new InvoiceDraftPage(VoucherType.Sales)),
                "빠른 메뉴 이동");

        var createPurchaseInvoiceButton = GeoraePlanTheme.CreateButton("구매 작성", GeoraePlanTheme.Brown);
        createPurchaseInvoiceButton.IsVisible = canCreateInvoices;
        createPurchaseInvoiceButton.IsEnabled = canCreateInvoices;
        createPurchaseInvoiceButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(new InvoiceDraftPage(VoucherType.Purchase)),
                "빠른 메뉴 이동");

        var canCreatePayments = _sessionStore.GetSnapshot().CanCreatePayments;
        var createPaymentButton = GeoraePlanTheme.CreateButton("수금/지급", GeoraePlanTheme.Purple);
        createPaymentButton.IsVisible = canCreatePayments;
        createPaymentButton.IsEnabled = canCreatePayments;
        createPaymentButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<PaymentDraftPage>()),
                "빠른 메뉴 이동");

        var inventoryTransferButton = GeoraePlanTheme.CreateButton("재고이동 조회", GeoraePlanTheme.Accent);
        inventoryTransferButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<InventoryTransfersPage>()),
                "빠른 메뉴 이동");

        var rentalsButton = GeoraePlanTheme.CreateButton("렌탈 조회", GeoraePlanTheme.SecondaryButton);
        rentalsButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<RentalsPage>()),
                "빠른 메뉴 이동");

        var canManageRecycleBin = _sessionStore.GetSnapshot().CanManageRecycleBin;
        var recycleBinButton = GeoraePlanTheme.CreateButton("휴지통", GeoraePlanTheme.SecondaryButton);
        recycleBinButton.IsVisible = canManageRecycleBin;
        recycleBinButton.IsEnabled = canManageRecycleBin;
        recycleBinButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<RecycleBinPage>()),
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
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            },
            ColumnSpacing = 12,
            RowSpacing = 12
        };
        quickActionGrid.Add(createSalesInvoiceButton, 0, 0);
        quickActionGrid.Add(createPurchaseInvoiceButton, 1, 0);
        quickActionGrid.Add(createPaymentButton, 0, 1);
        quickActionGrid.Add(inventoryTransferButton, 1, 1);
        quickActionGrid.Add(rentalsButton, 0, 2);
        quickActionGrid.Add(recycleBinButton, 1, 2);

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
                        GeoraePlanTheme.CreateBodyText("모바일에서 판매·구매·수금/지급은 입력 가능하며, 재고이동·렌탈은 조회 전용입니다. 생성·수정·확정은 PC에서 처리하세요."),
                        GeoraePlanTheme.CreateBodyText("거래처, 품목, 전표, 수금/지급, 재고이동, 렌탈 화면은 같은 거래플랜 운영 서버 sync 데이터를 기준으로 동작합니다."),
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
                if (!await _sessionStore.HasUsableSessionAsync())
                {
                    App.ShowLogin();
                    return;
                }

                await _viewModel.RefreshAsync();
                MobileErrorHandler.FireAndForget(
                    async () =>
                    {
                        await _syncCoordinator.RefreshIfServerChangedAsync("home-page", TimeSpan.FromSeconds(5));
                        await _viewModel.RefreshAsync();
                    },
                    "홈 화면 백그라운드 동기화");
            },
            "홈 화면 초기화");
    }

    private void HandleRealtimeRefreshRequested(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
            MobileErrorHandler.FireAndForget(
                async () =>
                {
                    if (Shell.Current?.CurrentPage == this)
                        await _viewModel.RefreshAsync();
                },
                "홈 실시간 갱신"));
    }
}
