using GeoraePlan.Mobile.App.Pages;

namespace GeoraePlan.Mobile.App;

public sealed class AppShell : Shell
{
    public AppShell()
    {
        Title = "거래플랜";
        FlyoutBehavior = FlyoutBehavior.Disabled;

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
