using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalAssetWindow : Window
{
    private readonly RentalAssetViewModel _viewModel;

    public RentalAssetWindow(RentalAssetViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12)
            return;

        Close();
        e.Handled = true;
    }

    private async void CustomerLookupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LookupWindow(
            "거래처 조회",
            await _viewModel.BuildCustomerLookupRowsAsync(),
            "거래처 등록",
            async () =>
            {
                var customerVm = new CustomerEditViewModel(_viewModel.LocalStateService, _viewModel.SessionState);
                await customerVm.LoadAsync();
                var customerWindow = new CustomerEditWindow(customerVm) { Owner = this };
                customerWindow.ShowDialog();
                return await _viewModel.BuildCustomerLookupRowsAsync();
            })
        { Owner = this };

        if (dialog.ShowDialog() == true && dialog.SelectedRow?.Tag is LocalCustomer customer)
            _viewModel.ApplySelectedCustomer(customer);
    }

    private async void ItemLookupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LookupWindow(
            "재고관리 품목 조회",
            await _viewModel.BuildItemLookupRowsAsync(),
            "품목 등록",
            async () =>
            {
                var inventoryVm = new InventoryViewModel(_viewModel.LocalStateService, _viewModel.SessionState);
                await inventoryVm.LoadAsync();
                inventoryVm.NewItemCommand.Execute(null);
                var inventoryWindow = new InventoryWindow(inventoryVm) { Owner = this };
                inventoryWindow.ShowDialog();
                return await _viewModel.BuildItemLookupRowsAsync();
            })
        { Owner = this };

        if (dialog.ShowDialog() == true && dialog.SelectedRow?.Tag is LocalItem item)
            await _viewModel.ApplySelectedItemAsync(item);
    }

    private async void PurchaseVendorLookupButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new LookupWindow(
            "매입처 조회",
            await _viewModel.BuildPurchaseVendorLookupRowsAsync(),
            "거래처 등록",
            async () =>
            {
                var customerVm = new CustomerEditViewModel(_viewModel.LocalStateService, _viewModel.SessionState);
                await customerVm.LoadAsync();
                var customerWindow = new CustomerEditWindow(customerVm) { Owner = this };
                customerWindow.ShowDialog();
                return await _viewModel.BuildPurchaseVendorLookupRowsAsync();
            })
        { Owner = this };

        if (dialog.ShowDialog() == true && dialog.SelectedRow?.Tag is LocalCustomer customer)
            _viewModel.ApplySelectedPurchaseVendor(customer);
    }
}
