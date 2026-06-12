using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class LoginPage : ContentPage
{
    private readonly LoginViewModel _viewModel;

    public LoginPage()
    {
        GeoraePlanTheme.ApplyPage(this, "로그인");

        _viewModel = ServiceHelper.GetRequiredService<LoginViewModel>();
        _viewModel.LoginSucceeded -= HandleLoginSucceeded;
        _viewModel.LoginSucceeded += HandleLoginSucceeded;
        BindingContext = _viewModel;

        var usernameEntry = GeoraePlanTheme.CreateEntry("아이디");
        usernameEntry.SetBinding(Entry.TextProperty, nameof(LoginViewModel.Username));

        var passwordEntry = GeoraePlanTheme.CreateEntry("비밀번호", isPassword: true);
        passwordEntry.SetBinding(Entry.TextProperty, nameof(LoginViewModel.Password));

        var rememberUsernameCheck = new CheckBox
        {
            Color = GeoraePlanTheme.Accent,
            VerticalOptions = LayoutOptions.Center
        };
        rememberUsernameCheck.SetBinding(CheckBox.IsCheckedProperty, nameof(LoginViewModel.RememberUsername));

        var rememberPasswordCheck = new CheckBox
        {
            Color = GeoraePlanTheme.Accent,
            VerticalOptions = LayoutOptions.Center
        };
        rememberPasswordCheck.SetBinding(CheckBox.IsCheckedProperty, nameof(LoginViewModel.RememberPassword));

        var rememberUsernameLabel = GeoraePlanTheme.CreateBodyText("아이디 저장", muted: false);
        var rememberPasswordLabel = GeoraePlanTheme.CreateBodyText("비밀번호 저장", muted: false);

        var rememberGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(new GridLength(12)),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 0,
            Margin = new Thickness(0, 4, 0, 0)
        };

        rememberGrid.Add(rememberUsernameCheck);
        Grid.SetColumn(rememberUsernameCheck, 0);
        rememberGrid.Add(rememberUsernameLabel);
        Grid.SetColumn(rememberUsernameLabel, 1);
        rememberGrid.Add(rememberPasswordCheck);
        Grid.SetColumn(rememberPasswordCheck, 3);
        rememberGrid.Add(rememberPasswordLabel);
        Grid.SetColumn(rememberPasswordLabel, 4);

        var loginButton = GeoraePlanTheme.CreateButton("로그인", GeoraePlanTheme.Accent);
        loginButton.SetBinding(Button.CommandProperty, nameof(LoginViewModel.LoginCommand));

        var activity = new ActivityIndicator { Color = GeoraePlanTheme.Accent };
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(LoginViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(LoginViewModel.IsBusy));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(LoginViewModel.StatusMessage));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 20,
                Spacing = 16,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label
                    {
                        Text = "거래플랜",
                        TextColor = GeoraePlanTheme.Accent,
                        FontSize = 30,
                        FontAttributes = FontAttributes.Bold,
                        HorizontalTextAlignment = TextAlignment.Center
                    },
                    new Label
                    {
                        Text = "거래플랜 운영 서버에 연결됩니다.",
                        TextColor = GeoraePlanTheme.TextSecondary,
                        HorizontalTextAlignment = TextAlignment.Center
                    },
                    GeoraePlanTheme.CreateCard(
                        GeoraePlanTheme.CreateSectionTitle("계정 로그인"),
                        GeoraePlanTheme.CreateBodyText("앱 연결 정보는 관리자 설정으로 고정되어 있습니다."),
                        usernameEntry,
                        passwordEntry,
                        rememberGrid,
                        loginButton,
                        activity,
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
await _viewModel.InitializeAsync();
            },
            "로그인 화면 초기화");
    }

    private static void HandleLoginSucceeded()
        => MainThread.BeginInvokeOnMainThread(App.ShowShell);
}
