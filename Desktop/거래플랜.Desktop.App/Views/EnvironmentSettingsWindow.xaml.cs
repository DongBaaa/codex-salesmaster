using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public enum EnvironmentSettingsInitialTab
{
    General,
    RecycleBin,
    Sync
}

public partial class EnvironmentSettingsWindow : Window
{
    public EnvironmentSettingsWindow(EnvironmentSettingsViewModel vm, EnvironmentSettingsInitialTab initialTab = EnvironmentSettingsInitialTab.General)
    {
        InitializeComponent();
        DataContext = vm;

        Loaded += (_, _) =>
        {
            SettingsTabs.SelectedItem = initialTab switch
            {
                EnvironmentSettingsInitialTab.RecycleBin => RecycleBinTab,
                EnvironmentSettingsInitialTab.Sync => SyncTab,
                _ => SettingsTabs.SelectedItem
            };
        };
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogWindowCloseHelper.Close(this);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
        {
            DialogWindowCloseHelper.Close(this);
            e.Handled = true;
        }
    }
}
