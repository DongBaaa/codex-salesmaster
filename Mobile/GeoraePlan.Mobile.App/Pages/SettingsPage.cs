using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage()
    {
        GeoraePlanTheme.ApplyPage(this, "설정");

        _viewModel = ServiceHelper.GetRequiredService<SettingsViewModel>();
        _viewModel.LoggedOut -= HandleLoggedOut;
        _viewModel.LoggedOut += HandleLoggedOut;
        BindingContext = _viewModel;

        var recycleBinButton = GeoraePlanTheme.CreateButton("휴지통 보기", GeoraePlanTheme.Brown);
        recycleBinButton.SetBinding(VisualElement.IsVisibleProperty, nameof(SettingsViewModel.CanManageRecycleBin));
        recycleBinButton.SetBinding(VisualElement.IsEnabledProperty, nameof(SettingsViewModel.CanManageRecycleBin));
        recycleBinButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<RecycleBinPage>()),
                "설정 메뉴 이동");

        var checkUpdateButton = GeoraePlanTheme.CreateButton("업데이트 확인", GeoraePlanTheme.SecondaryButton);
        checkUpdateButton.SetBinding(Button.CommandProperty, nameof(SettingsViewModel.CheckForUpdatesCommand));

        var installUpdateButton = GeoraePlanTheme.CreateButton("APK 다운로드 / 설치", GeoraePlanTheme.Accent);
        installUpdateButton.TextColor = Colors.Black;
        installUpdateButton.SetBinding(Button.CommandProperty, nameof(SettingsViewModel.InstallUpdateCommand));
        installUpdateButton.SetBinding(Button.IsEnabledProperty, nameof(SettingsViewModel.IsUpdateAvailable));

        var logoutButton = GeoraePlanTheme.CreateButton("로그아웃", GeoraePlanTheme.Danger);
        logoutButton.SetBinding(Button.CommandProperty, nameof(SettingsViewModel.LogoutCommand));

        var integrityButton = GeoraePlanTheme.CreateButton("운영점검 / 무결성", GeoraePlanTheme.Accent);
        integrityButton.TextColor = Colors.Black;
        integrityButton.SetBinding(VisualElement.IsVisibleProperty, nameof(SettingsViewModel.CanViewIntegrityReport));
        integrityButton.Clicked += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await Shell.Current.Navigation.PushAsync(ServiceHelper.GetRequiredService<IntegrityReportPage>()),
                "운영점검 화면 이동");

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(SettingsViewModel.StatusMessage));

        var integrityAccessLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: true, fontSize: 12);
        integrityAccessLabel.SetBinding(Label.TextProperty, nameof(SettingsViewModel.IntegrityAccessText));

        var updateStatusLabel = GeoraePlanTheme.CreateStatusLabel();
        updateStatusLabel.SetBinding(Label.TextProperty, nameof(SettingsViewModel.UpdateStatusMessage));

        var updateNotesLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false);
        updateNotesLabel.SetBinding(Label.TextProperty, nameof(SettingsViewModel.UpdateNotes));

        var connectionModeLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false);
        connectionModeLabel.SetBinding(Label.TextProperty, nameof(SettingsViewModel.ConnectionModeText));

        var advancedConnectionButton = GeoraePlanTheme.CreateButton("고급 연결 설정", GeoraePlanTheme.SecondaryButton);
        advancedConnectionButton.SetBinding(Button.CommandProperty, nameof(SettingsViewModel.ToggleConnectionSettingsCommand));

        var baseUrlEntry = GeoraePlanTheme.CreateEntry("https://trade.2884.kr 또는 터널 URL");
        baseUrlEntry.Keyboard = Keyboard.Url;
        baseUrlEntry.SetBinding(Entry.TextProperty, nameof(SettingsViewModel.BaseUrl));
        baseUrlEntry.SetBinding(VisualElement.IsVisibleProperty, nameof(SettingsViewModel.IsConnectionSettingsVisible));

        var connectionHelpLabel = GeoraePlanTheme.CreateBodyText(
            "현장 터널/테스트 서버가 필요할 때만 변경하세요. 연결 테스트가 성공해야 저장되며, 접속 오류가 나면 운영 서버로 초기화할 수 있습니다.",
            muted: true,
            fontSize: 12);
        connectionHelpLabel.SetBinding(VisualElement.IsVisibleProperty, nameof(SettingsViewModel.IsConnectionSettingsVisible));

        var saveConnectionButton = GeoraePlanTheme.CreateButton("연결 URL 저장", GeoraePlanTheme.Accent);
        saveConnectionButton.TextColor = Colors.Black;
        saveConnectionButton.SetBinding(Button.CommandProperty, nameof(SettingsViewModel.SaveCommand));
        saveConnectionButton.SetBinding(VisualElement.IsVisibleProperty, nameof(SettingsViewModel.IsConnectionSettingsVisible));

        var testConnectionButton = GeoraePlanTheme.CreateButton("연결 테스트", GeoraePlanTheme.Purple);
        testConnectionButton.SetBinding(Button.CommandProperty, nameof(SettingsViewModel.TestConnectionCommand));
        testConnectionButton.SetBinding(VisualElement.IsVisibleProperty, nameof(SettingsViewModel.IsConnectionSettingsVisible));

        var resetConnectionButton = GeoraePlanTheme.CreateButton("운영 서버로 초기화", GeoraePlanTheme.Brown);
        resetConnectionButton.SetBinding(Button.CommandProperty, nameof(SettingsViewModel.ResetConnectionCommand));
        resetConnectionButton.SetBinding(VisualElement.IsVisibleProperty, nameof(SettingsViewModel.IsConnectionSettingsVisible));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    GeoraePlanTheme.CreateCard(
                        GeoraePlanTheme.CreateSectionTitle("앱 설정"),
                        GeoraePlanTheme.CreateBodyText("모바일 앱은 기본적으로 거래플랜 운영 서버에 연결됩니다."),
                        connectionModeLabel,
                        advancedConnectionButton,
                        baseUrlEntry,
                        connectionHelpLabel,
                        testConnectionButton,
                        saveConnectionButton,
                        resetConnectionButton,
                        integrityAccessLabel,
                        integrityButton,
                        recycleBinButton,
                        logoutButton,
                        statusLabel),

                    GeoraePlanTheme.CreateCard(
                        GeoraePlanTheme.CreateSectionTitle("버전 / 업데이트"),
                        CreateInfoRow("현재 버전", nameof(SettingsViewModel.CurrentVersion)),
                        CreateInfoRow("최신 버전", nameof(SettingsViewModel.LatestVersion)),
                        updateNotesLabel,
                        checkUpdateButton,
                        installUpdateButton,
                        updateStatusLabel)
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
await _viewModel.LoadAsync();
            },
            "설정 화면 초기화");
    }

    private static Grid CreateInfoRow(string labelText, string bindingPath)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 10
        };

        grid.Add(GeoraePlanTheme.CreateBodyText(labelText), 0, 0);

        var value = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false);
        value.SetBinding(Label.TextProperty, bindingPath);
        grid.Add(value, 1, 0);
        return grid;
    }

    private static void HandleLoggedOut()
        => MainThread.BeginInvokeOnMainThread(App.ShowLogin);
}
