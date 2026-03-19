namespace GeoraePlan.Mobile.App;

public sealed class App : Application
{
    private readonly Services.SessionStore _sessionStore;

    public App(Services.SessionStore sessionStore)
    {
        _sessionStore = sessionStore;
        UserAppTheme = AppTheme.Light;
        MainPage = CreateRootPage();
    }

    private Page CreateRootPage()
        => _sessionStore.HasCachedSession()
            ? new AppShell()
            : new NavigationPage(new Pages.LoginPage());

    public static void ShowShell()
    {
        if (Current is null)
            return;

        Current.MainPage = new AppShell();
    }

    public static void ShowLogin()
    {
        if (Current is null)
            return;

        Current.MainPage = new NavigationPage(new Pages.LoginPage());
    }
}
