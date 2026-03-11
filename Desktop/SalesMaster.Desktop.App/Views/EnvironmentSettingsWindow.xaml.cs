using System.Windows;
using System.Windows.Input;
using SalesMaster.Desktop.App.ViewModels;

namespace SalesMaster.Desktop.App.Views;

public partial class EnvironmentSettingsWindow : Window
{
    public EnvironmentSettingsWindow(EnvironmentSettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
        {
            Close();
            e.Handled = true;
        }
    }
}
