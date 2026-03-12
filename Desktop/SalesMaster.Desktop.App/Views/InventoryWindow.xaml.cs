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

    private async void InventoryTransferButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not InventoryViewModel vm)
            return;

        var transferVm = new InventoryTransferViewModel(vm.LocalStateService, vm.SessionState);
        await transferVm.LoadAsync();

        var window = new InventoryTransferWindow(transferVm) { Owner = this };
        window.Closed += async (_, _) => await vm.LoadAsync();
        window.Show();
        window.Activate();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
