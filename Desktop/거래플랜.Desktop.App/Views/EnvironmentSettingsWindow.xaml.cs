using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class EnvironmentSettingsWindow : Window
{
    public EnvironmentSettingsWindow(EnvironmentSettingsViewModel vm, bool openRecycleBinTab = false)
    {
        InitializeComponent();
        DataContext = vm;

        if (openRecycleBinTab)
        {
            Loaded += (_, _) => SettingsTabs.SelectedItem = RecycleBinTab;
        }
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
