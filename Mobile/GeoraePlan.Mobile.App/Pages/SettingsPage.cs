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

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(SettingsViewModel.StatusMessage));

        var updateStatusLabel = GeoraePlanTheme.CreateStatusLabel();
        updateStatusLabel.SetBinding(Label.TextProperty, nameof(SettingsViewModel.UpdateStatusMessage));

        var updateNotesLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false);
        updateNotesLabel.SetBinding(Label.TextProperty, nameof(SettingsViewModel.UpdateNotes));

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
                        GeoraePlanTheme.CreateBodyText("모바일 앱은 거래플랜 NAS 서버에 고정 연결됩니다."),
                        GeoraePlanTheme.CreateBodyText("연결 정보는 사용자 화면에 표시하지 않습니다."),
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
