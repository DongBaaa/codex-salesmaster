using GeoraePlan.Mobile.App.Pages;
using GeoraePlan.Mobile.App.Theme;

namespace GeoraePlan.Mobile.App;

public sealed class AppShell : Shell
{
    private readonly Stack<ShellSection> _mainTabHistory = new();
    private ShellSection? _currentMainTab;
    private bool _isRestoringMainTab;

    public AppShell()
    {
        Title = "거래플랜";
        FlyoutBehavior = FlyoutBehavior.Disabled;
        BackgroundColor = GeoraePlanTheme.PageBackground;
        SetValue(Shell.BackgroundColorProperty, GeoraePlanTheme.Surface);
        SetValue(Shell.ForegroundColorProperty, GeoraePlanTheme.Accent);
        SetValue(Shell.TitleColorProperty, GeoraePlanTheme.TextPrimary);
        SetValue(Shell.UnselectedColorProperty, GeoraePlanTheme.TextSecondary);
        SetValue(Shell.TabBarBackgroundColorProperty, GeoraePlanTheme.Surface);
        SetValue(Shell.TabBarForegroundColorProperty, GeoraePlanTheme.Accent);
        SetValue(Shell.TabBarTitleColorProperty, GeoraePlanTheme.TextPrimary);
        SetValue(Shell.TabBarUnselectedColorProperty, GeoraePlanTheme.TextSecondary);
        SetValue(Shell.NavBarHasShadowProperty, false);

        Items.Add(new TabBar
        {
            Items =
            {
                CreateTab<HomePage>("홈", "tab_home.png"),
                CreateTab<CustomersPage>("거래처", "tab_customers.png"),
                CreateTab<ItemsPage>("품목", "tab_items.png"),
                CreateTab<InvoicesPage>("전표", "tab_invoices.png"),
                CreateTab<SyncPage>("동기화"),
                CreateTab<SettingsPage>("설정")
            }
        });

        Navigated += (_, _) => TrackMainTabHistory();
    }

    public bool TryNavigateToPreviousMainTab()
    {
        var tabBar = Items.OfType<TabBar>().FirstOrDefault();
        if (tabBar?.CurrentItem is null)
            return false;

        var current = tabBar.CurrentItem;
        while (_mainTabHistory.Count > 0)
        {
            var previous = _mainTabHistory.Pop();
            if (previous == current)
                continue;

            _isRestoringMainTab = true;
            tabBar.CurrentItem = previous;
            return true;
        }

        var home = tabBar.Items.FirstOrDefault();
        if (home is not null && current != home)
        {
            _isRestoringMainTab = true;
            tabBar.CurrentItem = home;
            return true;
        }

        return false;
    }

    protected override bool OnBackButtonPressed()
    {
        var navigation = CurrentPage?.Navigation;
        if ((navigation?.ModalStack.Count ?? 0) > 0 || (navigation?.NavigationStack.Count ?? 0) > 1)
            return base.OnBackButtonPressed();

        if (TryNavigateToPreviousMainTab())
            return true;

        return base.OnBackButtonPressed();
    }

    private void TrackMainTabHistory()
    {
        var tabBar = Items.OfType<TabBar>().FirstOrDefault();
        var current = tabBar?.CurrentItem;
        if (current is null)
            return;

        if (_currentMainTab is not null && _currentMainTab != current && !_isRestoringMainTab)
            _mainTabHistory.Push(_currentMainTab);

        _currentMainTab = current;
        _isRestoringMainTab = false;
    }

    private static ShellContent CreateTab<TPage>(string title, string? icon = null) where TPage : Page, new()
    {
        return new ShellContent
        {
            Title = title,
            Icon = icon,
            ContentTemplate = new DataTemplate(typeof(TPage))
        };
    }
}
