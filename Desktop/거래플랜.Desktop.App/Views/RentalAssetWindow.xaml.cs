using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;

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
        DialogWindowCloseHelper.Close(this);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12)
            return;

        DialogWindowCloseHelper.Close(this);
        e.Handled = true;
    }

    private void CustomerLookupButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(this, OpenCustomerLookupAsync, "UI", "렌탈 자산 거래처 조회", "거래처 조회 중 오류가 발생했습니다.");

    private async Task OpenCustomerLookupAsync()
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

    private void ItemLookupButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(this, OpenItemLookupAsync, "UI", "렌탈 자산 품목 조회", "자산 품목 조회 중 오류가 발생했습니다.");

    private async Task OpenItemLookupAsync()
    {
        var dialog = new LookupWindow(
            "자산 품목 조회",
            await _viewModel.BuildItemLookupRowsAsync(),
            "자산 품목 등록",
            async () =>
            {
                var inventoryVm = new InventoryViewModel(_viewModel.LocalStateService, _viewModel.SessionState);
                await inventoryVm.LoadAsync();
                inventoryVm.SelectedTrackingTypeFilter = ItemTrackingTypes.Asset;
                inventoryVm.NewItemCommand.Execute(null);
                inventoryVm.EditTrackingType = ItemTrackingTypes.Asset;
                var inventoryWindow = new InventoryWindow(inventoryVm) { Owner = this };
                inventoryWindow.ShowDialog();
                return await _viewModel.BuildItemLookupRowsAsync();
            })
        { Owner = this };

        if (dialog.ShowDialog() == true && dialog.SelectedRow?.Tag is LocalItem item)
            await _viewModel.ApplySelectedItemAsync(item);
    }

    private void PurchaseVendorLookupButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(this, OpenPurchaseVendorLookupAsync, "UI", "렌탈 자산 매입처 조회", "매입처 조회 중 오류가 발생했습니다.");

    private async Task OpenPurchaseVendorLookupAsync()
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
