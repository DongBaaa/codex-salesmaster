using GeoraePlan.Mobile.App.Pages;
using GeoraePlan.Mobile.App.Theme;

namespace GeoraePlan.Mobile.App;

public sealed class AppShell : Shell
{
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
                CreateTab<HomePage>("홈"),
                CreateTab<CustomersPage>("거래처"),
                CreateTab<ItemsPage>("품목"),
                CreateTab<InvoicesPage>("전표"),
                CreateTab<SyncPage>("동기화"),
                CreateTab<SettingsPage>("설정")
            }
        });
    }

    private static ShellContent CreateTab<TPage>(string title) where TPage : Page, new()
    {
        return new ShellContent
        {
            Title = title,
            ContentTemplate = new DataTemplate(typeof(TPage))
        };
    }
}
