using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalAssetWindow : Window
{
    private readonly RentalAssetViewModel _viewModel;
    private bool _allowCloseWithoutSave;
    private bool _closeInProgress;

    public RentalAssetWindow(RentalAssetViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closing += Window_Closing;
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

    private void AssignmentHistoryGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject) is not { } row)
            return;

        row.IsSelected = true;
        AssignmentHistoryGrid.SelectedItem = row.Item;
    }

    private static T? FindVisualParent<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

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

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            if (_allowCloseWithoutSave)
                return;

            if (_closeInProgress)
            {
                e.Cancel = true;
                return;
            }

            if (!_viewModel.HasMeaningfulDraftContentForClose || !_viewModel.HasPendingChanges)
                return;

            e.Cancel = true;
            _closeInProgress = true;
            var requestDeferredClose = false;
            var previousCursor = Mouse.OverrideCursor;
            try
            {
                IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;

                var saved = await _viewModel.TryAutoSaveOnCloseAsync();
                if (saved)
                {
                    _allowCloseWithoutSave = true;
                    requestDeferredClose = true;
                }
                else
                {
                    var discard = MessageBox.Show(
                        $"{_viewModel.StatusMessage}\n\n저장되지 않은 변경사항이 있습니다. 저장 없이 닫을까요?",
                        "확인",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (discard == MessageBoxResult.Yes)
                    {
                        _allowCloseWithoutSave = true;
                        requestDeferredClose = true;
                    }
                }
            }
            finally
            {
                Mouse.OverrideCursor = previousCursor;
                if (!_allowCloseWithoutSave)
                    IsEnabled = true;
                _closeInProgress = false;
            }

            if (requestDeferredClose)
                _ = Dispatcher.BeginInvoke(new Action(() => DialogWindowCloseHelper.Close(this)));
        }
        catch (Exception ex)
        {
            AppLogger.Error("UI", "렌탈 자산 창 닫기 처리 실패", ex);
            e.Cancel = true;
            IsEnabled = true;
            Mouse.OverrideCursor = null;
            _closeInProgress = false;

            MessageBox.Show(
                this,
                $"렌탈 자산 창을 닫는 중 오류가 발생했습니다.{Environment.NewLine}{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
