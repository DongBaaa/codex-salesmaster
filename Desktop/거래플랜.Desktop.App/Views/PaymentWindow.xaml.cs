using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class PaymentWindow : Window
{
    private readonly PaymentViewModel _vm;

    public PaymentWindow(PaymentViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
        {
            DialogWindowCloseHelper.Close(this);
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogWindowCloseHelper.Close(this);

    private void CustomerSelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanSelectCustomer)
            return;

        var dlg = new LookupWindow(
            "거래처 검색",
            BuildCustomerRows(),
            "거래처 등록",
            async () =>
            {
                var customerVm = new CustomerEditViewModel(_vm.LocalStateService, _vm.SessionState);
                await customerVm.LoadAsync();
                var customerWindow = new CustomerEditWindow(customerVm) { Owner = this };
                customerWindow.ShowDialog();

                await _vm.ReloadCustomersAsync();
                return BuildCustomerRows();
            })
        { Owner = this };

        if (dlg.ShowDialog() == true && dlg.SelectedRow?.Tag is LocalCustomer selected)
        {
            _vm.SetCustomer(selected);
        }
    }

    private List<LookupRow> BuildCustomerRows()
    {
        return _vm.GetAllCustomers()
            .Select(c => new LookupRow
            {
                Id = c.Id,
                PrimaryText = c.NameOriginal,
                SecondaryText = c.Phone,
                Tag = c
            })
            .ToList();
    }

    private void HistoryDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (FindAncestor<DataGridColumnHeader>(source) is not null ||
            FindAncestor<ScrollBar>(source) is not null ||
            FindAncestor<Button>(source) is not null ||
            FindAncestor<CheckBox>(source) is not null ||
            FindAncestor<ComboBox>(source) is not null)
        {
            return;
        }

        if (FindAncestor<DataGridRow>(source) is not DataGridRow row ||
            row.Item is not LocalTransaction transaction)
        {
            return;
        }

        _vm.SelectedHistory = transaction;
        UiTaskHelper.Run(
            this,
            () => _vm.LoadSelectedHistoryIntoEditorAsync(),
            "PAYMENT",
            "수금/지급 내역 더블클릭 수정",
            "수금/지급 내역을 수정 모드로 불러오는 중 오류가 발생했습니다.");
        e.Handled = true;
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
