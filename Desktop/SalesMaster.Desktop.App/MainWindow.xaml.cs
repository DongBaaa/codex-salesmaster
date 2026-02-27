using System.Linq;
using System.Windows;
using System.Windows.Input;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Desktop.App.ViewModels;
using SalesMaster.Desktop.App.Views;

namespace SalesMaster.Desktop.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly LocalStateService _local;
    private readonly StatementPrintService _print;
    private readonly IPrintService _invoicePrintService;
    private readonly SessionState _session;

    public MainWindow(MainViewModel vm, LocalStateService local,
                      StatementPrintService print,
                      IPrintService invoicePrintService,
                      SessionState session)
    {
        InitializeComponent();
        _vm = vm;
        _local = local;
        _print = print;
        _invoicePrintService = invoicePrintService;
        _session = session;
        DataContext = vm;
    }

    public async Task InitAsync()
    {
        await _vm.LoadAsync();
    }

    // F9 -> 거래명세서 인쇄
    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9)
        {
            if (_vm.PrintStatementCommand.CanExecute(null))
                _vm.PrintStatementCommand.Execute(null);
            e.Handled = true;
        }
    }

    // 판매작성 (리스트 툴바 버튼)
    private async void SalesToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session);
        await vm.LoadAsync();
        vm.NewInvoice();

        // 현재 선택 거래처가 있으면 미리 세팅
        if (_vm.SelectedCustomerFilter is not null)
            vm.SetCustomer(_vm.SelectedCustomerFilter);

        var win = new SalesWindow(vm) { Owner = this };
        win.Closed += async (_, _) => await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
        win.Show();
    }

    // 전표 수정 버튼
    private async void EditInvoiceButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenSelectedInvoiceEditorAsync();
    }

    // 전표 목록 더블클릭 수정
    private async void InvoiceRowsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await OpenSelectedInvoiceEditorAsync();
    }

    private async Task OpenSelectedInvoiceEditorAsync()
    {
        if (_vm.SelectedInvoiceRow is null)
        {
            MessageBox.Show("수정할 전표를 선택하세요.", "알림", MessageBoxButton.OK);
            return;
        }

        var inv = await _local.GetInvoiceAsync(_vm.SelectedInvoiceRow.Id);
        if (inv is null) return;

        var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session);
        await vm.LoadAsync();
        vm.LoadInvoice(inv);

        var win = new SalesWindow(vm) { Owner = this };
        win.Closed += async (_, _) => await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
        win.Show();
    }

    // 거래처 우클릭 -> 거래처 수정
    private async void CustomerEditContextMenu_Click(object sender, RoutedEventArgs e)
    {
        var customer = _vm.SelectedCustomerFilter;
        if (customer is null) return;

        var vm = new CustomerEditViewModel(_local);
        await vm.LoadAsync(customer);
        var win = new CustomerEditWindow(vm) { Owner = this };
        if (win.ShowDialog() == true)
            await _vm.RefreshCustomersCommand.ExecuteAsync(null);
    }

    // 재고관리 버튼
    private async void InventoryButton_Click(object sender, RoutedEventArgs e)
    {
        var vm = new InventoryViewModel(_local);
        await vm.LoadAsync();
        var win = new InventoryWindow(vm) { Owner = this };
        win.Show();
    }

    // 거래처등록 버튼
    private async void CustomerEditButton_Click(object sender, RoutedEventArgs e)
    {
        var vm = new CustomerEditViewModel(_local);
        await vm.LoadAsync();
        var win = new CustomerEditWindow(vm) { Owner = this };
        if (win.ShowDialog() == true)
            await _vm.RefreshCustomersCommand.ExecuteAsync(null);
    }

    // 판매작성 버튼(헤더)
    private async void SalesButton_Click(object sender, RoutedEventArgs e)
    {
        var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session);
        await vm.LoadAsync();
        vm.NewInvoice();
        var win = new SalesWindow(vm) { Owner = this };
        win.Closed += async (_, _) => await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
        win.Show();
    }

    // 수금지불 버튼(헤더)
    private async void PaymentButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenPaymentPopupAsync();
    }

    // 전표 목록 탭의 수금 입력 버튼
    private async void PaymentEntryButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenPaymentPopupAsync();
    }

    private async Task OpenPaymentPopupAsync()
    {
        var vm = new PaymentViewModel(_local);

        // 우선: 좌측 거래처 선택값, 없으면 선택 전표의 거래처를 사용
        var preselect = _vm.SelectedCustomerFilter;
        if (preselect is null && _vm.SelectedInvoiceRow is not null)
        {
            var invoice = await _local.GetInvoiceAsync(_vm.SelectedInvoiceRow.Id);
            if (invoice is not null)
            {
                var customers = await _local.GetCustomersAsync();
                preselect = customers.FirstOrDefault(c => c.Id == invoice.CustomerId);
            }
        }

        await vm.LoadAsync(preselect);
        var win = new PaymentWindow(vm) { Owner = this };
        win.Show();
    }
}
