using System.Windows;
using System.Windows.Input;
using SalesMaster.Desktop.App.ViewModels;

namespace SalesMaster.Desktop.App.Views;

public partial class InventoryWindow : Window
{
    public InventoryWindow(InventoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12) { Close(); e.Handled = true; }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
