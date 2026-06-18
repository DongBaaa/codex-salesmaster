using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Views;

public partial class CustomerManagementWindow : Window
{
    private readonly CustomerManagementViewModel _vm;
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private readonly ErpApiClient? _api;

    public CustomerManagementWindow(
        CustomerManagementViewModel vm,
        LocalStateService local,
        SessionState session,
        ErpApiClient? api = null)
    {
        InitializeComponent();
        _vm = vm;
        _local = local;
        _session = session;
        _api = api;
        DataContext = vm;
        Closed += (_, _) => _vm.Dispose();
    }

    private void CreateCustomerButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskHelper.Run(this, async () =>
        {
            var customerVm = new CustomerEditViewModel(_local, _session, _api);
            await customerVm.LoadAsync();
            var win = new CustomerEditWindow(customerVm) { Owner = this };
            if (win.ShowDialog() == true)
                await _vm.ReloadCommand.ExecuteAsync(null);
        }, "UI", "거래처 등록 창 열기", "거래처 등록 창을 여는 중 오류가 발생했습니다.");
    }

    private void EditCustomerButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(this, OpenSelectedCustomerEditorAsync, "UI", "거래처 수정 창 열기", "거래처 수정 창을 여는 중 오류가 발생했습니다.");

    private void DeleteCustomerButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(this, DeleteSelectedCustomerAsync, "UI", "거래처 삭제", "거래처를 삭제하는 중 오류가 발생했습니다.");

    private void CustomerRowsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (FindAncestor<ComboBox>(source) is not null || FindAncestor<Button>(source) is not null)
            return;

        if (FindAncestor<DataGridRow>(source) is null)
            return;

        UiTaskHelper.Run(this, OpenSelectedCustomerEditorAsync, "UI", "거래처 상세 더블클릭 열기", "거래처 상세를 여는 중 오류가 발생했습니다.");
    }

    private void ResponsibleOfficeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox comboBox || !comboBox.IsLoaded)
            return;

        if (comboBox.DataContext is not EnvironmentCustomerRow row || !row.IsModified)
            return;

        UiTaskHelper.Run(
            this,
            () => _vm.SaveOfficeChangeAsync(row),
            "UI",
            "거래처 담당지점 즉시 저장",
            "담당지점을 저장하는 중 오류가 발생했습니다.");
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

    private async Task OpenSelectedCustomerEditorAsync()
    {
        if (_vm.SelectedCustomer is null)
        {
            MessageBox.Show("수정할 거래처를 먼저 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var customerVm = new CustomerEditViewModel(_local, _session, _api);
        await customerVm.LoadAsync(_vm.SelectedCustomer.Source);
        var win = new CustomerEditWindow(customerVm) { Owner = this };
        if (win.ShowDialog() == true)
            await _vm.ReloadCommand.ExecuteAsync(null);
    }

    private async Task DeleteSelectedCustomerAsync()
    {
        if (_vm.SelectedCustomer is null)
        {
            MessageBox.Show("삭제할 거래처를 먼저 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var selectedCustomer = _vm.SelectedCustomer;
        var confirm = MessageBox.Show(
            $"거래처 '{selectedCustomer.NameOriginal}'을(를) 삭제하시겠습니까?\n삭제 후에는 목록에서 숨겨지고 관련 계약도 함께 비활성화됩니다.",
            "거래처 삭제",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes)
            return;

        var result = await _local.DeleteCustomerAsync(selectedCustomer.Id, _session, selectedCustomer.Source.Revision);

        if (!result.Success)
        {
            MessageBox.Show(result.Message, "거래처 삭제", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(result.Message, "거래처 삭제", MessageBoxButton.OK, MessageBoxImage.Information);
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
