using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class InventoryWindow : Window
{
    public InventoryWindow(InventoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Activated += InventoryWindow_Activated;
    }

    private async void InventoryWindow_Activated(object? sender, EventArgs e)
    {
        if (DataContext is not InventoryViewModel vm)
            return;

        await vm.ReloadItemCategoryOptionsAsync();
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
