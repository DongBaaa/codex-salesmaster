using GeoraePlan.Mobile.App.ViewModels;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class SettingsPage : ContentPage
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage()
    {
        Title = "설정";
        _viewModel = ServiceHelper.GetRequiredService<SettingsViewModel>();
        _viewModel.LoggedOut -= HandleLoggedOut;
        _viewModel.LoggedOut += HandleLoggedOut;
        BindingContext = _viewModel;

        var baseUrlEntry = new Entry { Placeholder = "https://api.example.invalid" };
        baseUrlEntry.SetBinding(Entry.TextProperty, nameof(SettingsViewModel.BaseUrl));

        var saveButton = new Button { Text = "서버 주소 저장" };
        saveButton.SetBinding(Button.CommandProperty, nameof(SettingsViewModel.SaveCommand));

        var logoutButton = new Button
        {
            Text = "로그아웃",
            BackgroundColor = Colors.IndianRed,
            TextColor = Colors.White
        };
        logoutButton.SetBinding(Button.CommandProperty, nameof(SettingsViewModel.LogoutCommand));

        var statusLabel = new Label { TextColor = Colors.DimGray };
        statusLabel.SetBinding(Label.TextProperty, nameof(SettingsViewModel.StatusMessage));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 16,
                Children =
                {
                    new Label { Text = "NAS 서버 주소", FontAttributes = FontAttributes.Bold },
                    baseUrlEntry,
                    saveButton,
                    new BoxView { HeightRequest = 1, Color = Colors.LightGray },
                    logoutButton,
                    statusLabel
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.LoadAsync();
    }

    private static void HandleLoggedOut()
        => MainThread.BeginInvokeOnMainThread(App.ShowLogin);
}
