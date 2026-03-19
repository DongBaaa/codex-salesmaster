using GeoraePlan.Mobile.App.ViewModels;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class LoginPage : ContentPage
{
    private readonly LoginViewModel _viewModel;

    public LoginPage()
    {
        Title = "로그인";
        _viewModel = ServiceHelper.GetRequiredService<LoginViewModel>();
        _viewModel.LoginSucceeded -= HandleLoginSucceeded;
        _viewModel.LoginSucceeded += HandleLoginSucceeded;
        BindingContext = _viewModel;

        var baseUrlEntry = new Entry { Placeholder = "https://api.example.invalid" };
        baseUrlEntry.SetBinding(Entry.TextProperty, nameof(LoginViewModel.BaseUrl));

        var usernameEntry = new Entry { Placeholder = "아이디" };
        usernameEntry.SetBinding(Entry.TextProperty, nameof(LoginViewModel.Username));

        var passwordEntry = new Entry { Placeholder = "비밀번호", IsPassword = true };
        passwordEntry.SetBinding(Entry.TextProperty, nameof(LoginViewModel.Password));

        var loginButton = new Button { Text = "로그인" };
        loginButton.SetBinding(Button.CommandProperty, nameof(LoginViewModel.LoginCommand));

        var activity = new ActivityIndicator();
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(LoginViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(LoginViewModel.IsBusy));

        var statusLabel = new Label { TextColor = Colors.DimGray };
        statusLabel.SetBinding(Label.TextProperty, nameof(LoginViewModel.StatusMessage));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 16,
                Children =
                {
                    new Label
                    {
                        Text = "거래플랜",
                        FontSize = 28,
                        FontAttributes = FontAttributes.Bold
                    },
                    new Label
                    {
                        Text = "NAS 서버 주소와 계정을 입력하세요."
                    },
                    new Label { Text = "서버 주소" },
                    baseUrlEntry,
                    new Label { Text = "아이디" },
                    usernameEntry,
                    new Label { Text = "비밀번호" },
                    passwordEntry,
                    loginButton,
                    activity,
                    statusLabel
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.InitializeAsync();
    }

    private static void HandleLoginSucceeded()
        => MainThread.BeginInvokeOnMainThread(App.ShowShell);
}
