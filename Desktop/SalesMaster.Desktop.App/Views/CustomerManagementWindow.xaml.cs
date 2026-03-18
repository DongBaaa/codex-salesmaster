using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Desktop.App.ViewModels;

namespace SalesMaster.Desktop.App.Views;

public partial class CustomerManagementWindow : Window
{
    private readonly CustomerManagementViewModel _vm;
    private readonly LocalStateService _local;
    private readonly SessionState _session;

    public CustomerManagementWindow(
        CustomerManagementViewModel vm,
        LocalStateService local,
        SessionState session)
    {
        InitializeComponent();
        _vm = vm;
        _local = local;
        _session = session;
        DataContext = vm;
    }

    private async void CreateCustomerButton_Click(object sender, RoutedEventArgs e)
    {
        var customerVm = new CustomerEditViewModel(_local, _session);
        await customerVm.LoadAsync();
        var win = new CustomerEditWindow(customerVm) { Owner = this };
        if (win.ShowDialog() == true)
            await _vm.ReloadCommand.ExecuteAsync(null);
    }

    private async void EditCustomerButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenSelectedCustomerEditorAsync();
    }

    private async void CustomerRowsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (FindAncestor<ComboBox>(source) is not null || FindAncestor<Button>(source) is not null)
            return;

        if (FindAncestor<DataGridRow>(source) is null)
            return;

        await OpenSelectedCustomerEditorAsync();
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

    private async Task OpenSelectedCustomerEditorAsync()
    {
        if (_vm.SelectedCustomer is null)
        {
            MessageBox.Show("수정할 거래처를 먼저 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var customerVm = new CustomerEditViewModel(_local, _session);
        await customerVm.LoadAsync(_vm.SelectedCustomer.Source);
        var win = new CustomerEditWindow(customerVm) { Owner = this };
        if (win.ShowDialog() == true)
            await _vm.ReloadCommand.ExecuteAsync(null);
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed)
                return typed;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
