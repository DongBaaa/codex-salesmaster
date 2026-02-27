using System.Linq;
using System.Windows;
using System.Windows.Input;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.ViewModels;

namespace SalesMaster.Desktop.App.Views;

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
            Close();
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void CustomerSelectButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new LookupWindow(
            "거래처 검색",
            BuildCustomerRows(),
            "거래처 등록",
            async () =>
            {
                var customerVm = new CustomerEditViewModel(_vm.LocalStateService);
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
}
