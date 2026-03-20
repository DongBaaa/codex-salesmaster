using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Views;

namespace 거래플랜.Desktop.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly LocalStateService _local;
    private readonly RentalStateService _rental;
    private readonly RentalDocumentService _rentalDocuments;
    private readonly StatementPrintService _print;
    private readonly IPrintService _invoicePrintService;
    private readonly SessionState _session;
    private readonly ErpApiClient _api;

    public MainWindow(MainViewModel vm, LocalStateService local,
                      RentalStateService rental,
                      RentalDocumentService rentalDocuments,
                      StatementPrintService print,
                      IPrintService invoicePrintService,
                      SessionState session,
                      ErpApiClient api)
    {
        InitializeComponent();
        _vm = vm;
        _local = local;
        _rental = rental;
        _rentalDocuments = rentalDocuments;
        _print = print;
        _invoicePrintService = invoicePrintService;
        _session = session;
        _api = api;
        DataContext = vm;
    }

    public async Task InitAsync()
    {
        await _vm.LoadAsync();

        var popupSections = new List<string>();
        if (!string.IsNullOrWhiteSpace(_vm.ContractAlertPopupMessage))
            popupSections.Add(_vm.ContractAlertPopupMessage);
        if (!string.IsNullOrWhiteSpace(_vm.RentalAlertPopupMessage))
            popupSections.Add(_vm.RentalAlertPopupMessage);

        if (popupSections.Count > 0)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine + Environment.NewLine, popupSections),
                "대시보드 알림",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    // F9: 거래명세서 인쇄, F6: 신규 판매작성
    // Ctrl+Shift+C: 거래처등록, Ctrl+Shift+I: 재고관리, Ctrl+Shift+P: 수금지불
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9)
        {
            if (_vm.PrintStatementCommand.CanExecute(null))
                _vm.PrintStatementCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F6)
        {
            await OpenSalesWindowAsync(preselectSelectedCustomer: true);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.C)
            {
                await OpenCustomerEditorAsync();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.I)
            {
                await OpenInventoryWindowAsync();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.P)
            {
                await OpenPaymentPopupAsync();
                e.Handled = true;
            }
        }
    }

    // 판매작성 (리스트 툴바 버튼)
    private async void SalesToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenSalesWindowAsync(preselectSelectedCustomer: true);
    }

    private async void PurchaseToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenPurchaseWindowAsync(preselectSelectedCustomer: true);
    }

    private async void ProcurementToolbarButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenProcurementWindowAsync(preselectSelectedCustomer: true);
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

        var inv = await _local.GetInvoiceAsync(_vm.SelectedInvoiceRow.Id, _session);
        if (inv is null) return;

        await OpenInvoiceWindowAsync(inv);
    }

    // 거래처 우클릭 -> 거래처 수정
    private async void CustomerEditContextMenu_Click(object sender, RoutedEventArgs e)
    {
        var customer = _vm.SelectedCustomerFilter;
        if (customer is null) return;
        await OpenCustomerEditorAsync(customer);
    }

    // 거래처 우클릭 -> 거래처 삭제
    private async void CustomerDeleteContextMenu_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedCustomerAsync();
    }

    // 거래처 더블클릭 -> 거래처 수정창 열기
    private async void CustomerListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var customer = _vm.SelectedCustomerFilter;
        if (customer is null)
            return;

        await OpenCustomerEditorAsync(customer);
    }

    private void CustomerListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var source = e.OriginalSource as DependencyObject;
        var item = FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is Data.LocalCustomer customer)
            listBox.SelectedItem = customer;
    }

    private async Task DeleteSelectedCustomerAsync()
    {
        var customer = _vm.SelectedCustomerFilter;
        if (customer is null)
        {
            MessageBox.Show("삭제할 거래처를 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var activeInvoices = await _local.GetInvoicesAsync(customerId: customer.Id);
        if (activeInvoices.Count > 0)
        {
            MessageBox.Show(
                $"해당 거래처 전표가 {activeInvoices.Count:N0}건 남아 있어 삭제할 수 없습니다.\n먼저 전표를 모두 삭제한 뒤 거래처를 삭제하세요.",
                "거래처 삭제",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"거래처 '{customer.NameOriginal}'를 삭제하시겠습니까?{Environment.NewLine}삭제된 항목은 환경설정 > 휴지통에서 복원할 수 있습니다.",
            "거래처 삭제 확인",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
            return;

        var deleteCustomerResult = await _local.DeleteCustomerAsync(customer.Id, _session);
        if (!deleteCustomerResult.Success)
        {
            MessageBox.Show(deleteCustomerResult.Message, "거래처 삭제", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await _vm.RefreshCustomersCommand.ExecuteAsync(null);
    }

    // 재고관리 버튼
    private async void InventoryButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenInventoryWindowAsync();
    }

    // 거래처등록 버튼
    private async void CustomerEditButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenCustomerEditorAsync();
    }

    // 거래처삭제 버튼
    private async void CustomerDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedCustomerAsync();
    }

    private async void CustomerManagementButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenCustomerManagementWindowAsync();
    }

    private async void DeleteSelectedInvoicesContextMenu_Click(object sender, RoutedEventArgs e)
    {
        var rows = GetSelectedInvoiceRows(sender).ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show("삭제할 전표를 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"선택한 전표를 삭제하시겠습니까?{Environment.NewLine}삭제된 전표는 환경설정 > 휴지통에서 복원할 수 있습니다.",
            "전표 삭제 확인",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
            return;

        foreach (var rowId in rows.Select(r => r.Id).Distinct())
        {
            var deleteInvoiceResult = await _local.DeleteInvoiceAsync(rowId, _session);
            if (!deleteInvoiceResult.Success)
            {
                MessageBox.Show(deleteInvoiceResult.Message, "전표 삭제", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            }
        }

        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private static IEnumerable<InvoiceListRow> GetSelectedInvoiceRows(object sender)
    {
        if (sender is not MenuItem menuItem)
            return Enumerable.Empty<InvoiceListRow>();

        if (menuItem.Parent is not ContextMenu contextMenu)
            return Enumerable.Empty<InvoiceListRow>();

        if (contextMenu.PlacementTarget is not DataGrid grid)
            return Enumerable.Empty<InvoiceListRow>();

        return grid.SelectedItems.OfType<InvoiceListRow>();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T found)
                return found;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    // 판매작성 버튼(헤더)
    private async void SalesButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenSalesWindowAsync(preselectSelectedCustomer: false);
    }

    // 수금지불 버튼(헤더)
    private async void PaymentButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenPaymentPopupAsync();
    }

    // 자료기간별 집계 버튼(헤더)
    private async void PeriodLedgerButton_Click(object sender, RoutedEventArgs e)
    {
        var vm = new PeriodLedgerViewModel(
            _local,
            new PeriodLedgerAggregationService(_local),
            new PeriodLedgerExcelExportService(),
            _session);

        await vm.InitializeAsync();
        var win = new PeriodLedgerWindow(vm) { Owner = this };
        win.ShowDialog();
    }

    private async void YeonsuDeliveryButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenYeonsuDeliveryWindowAsync();
    }

    private async void EnvironmentSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenEnvironmentSettingsWindowAsync();
    }

    private async void RecycleBinButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenEnvironmentSettingsWindowAsync(openRecycleBinTab: true);
    }

    private void RentalManagementButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
            return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private async void RentalDashboardMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await OpenRentalDashboardWindowAsync();
    }

    private async void RentalBillingMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await OpenRentalBillingWindowAsync();
    }

    private async void RentalAssetMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await OpenRentalAssetWindowAsync();
    }

    private async void RentalSettingsMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await OpenRentalSettingsWindowAsync();
    }

    // 전표 목록 탭의 수금 입력 버튼
    private async void PaymentEntryButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenPaymentPopupAsync();
    }

    private async Task OpenSalesWindowAsync(bool preselectSelectedCustomer)
    {
        await OpenNewInvoiceWindowAsync(거래플랜.Shared.Contracts.VoucherType.Sales, preselectSelectedCustomer);
    }

    private async Task OpenPurchaseWindowAsync(bool preselectSelectedCustomer)
    {
        await OpenNewInvoiceWindowAsync(거래플랜.Shared.Contracts.VoucherType.Purchase, preselectSelectedCustomer);
    }

    private async Task OpenProcurementWindowAsync(bool preselectSelectedCustomer)
    {
        await OpenNewInvoiceWindowAsync(거래플랜.Shared.Contracts.VoucherType.Procurement, preselectSelectedCustomer);
    }

    private async Task OpenNewInvoiceWindowAsync(
        거래플랜.Shared.Contracts.VoucherType voucherType,
        bool preselectSelectedCustomer)
    {
        var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session, voucherType);
        await vm.LoadAsync();
        vm.NewInvoice();

        if (preselectSelectedCustomer &&
            _vm.SelectedCustomerFilter is not null &&
            vm.CanSelectCustomer(_vm.SelectedCustomerFilter))
        {
            vm.SetCustomer(_vm.SelectedCustomerFilter);
            vm.MarkCurrentStateAsPristine();
        }

        var win = new SalesWindow(vm) { Owner = this };
        win.Closed += async (_, _) => await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
        win.Show();
    }

    private async Task OpenInvoiceWindowAsync(Data.LocalInvoice invoice)
    {
        var entryType = invoice.VoucherType switch
        {
            거래플랜.Shared.Contracts.VoucherType.Purchase => 거래플랜.Shared.Contracts.VoucherType.Purchase,
            거래플랜.Shared.Contracts.VoucherType.Procurement => 거래플랜.Shared.Contracts.VoucherType.Procurement,
            _ => 거래플랜.Shared.Contracts.VoucherType.Sales
        };

        var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session, entryType);
        await vm.LoadAsync();
        vm.LoadInvoice(invoice);

        var win = new SalesWindow(vm) { Owner = this };
        win.Closed += async (_, _) => await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
        win.Show();
    }

    private async Task OpenCustomerEditorAsync(Data.LocalCustomer? customer = null)
    {
        var vm = new CustomerEditViewModel(_local, _session);
        await vm.LoadAsync(customer);

        var win = new CustomerEditWindow(vm) { Owner = this };
        if (win.ShowDialog() == true)
            await _vm.RefreshCustomersCommand.ExecuteAsync(null);
    }

    private async Task OpenInventoryWindowAsync()
    {
        var vm = new InventoryViewModel(_local, _session);
        await vm.LoadAsync();
        var win = new InventoryWindow(vm) { Owner = this };
        win.Show();
    }

    private async Task OpenPaymentPopupAsync()
    {
        var vm = new PaymentViewModel(_local, _session);

        // 우선: 좌측 거래처 선택값, 없으면 선택 전표의 거래처를 사용
        var preselect = _vm.SelectedCustomerFilter;
        if (preselect is null && _vm.SelectedInvoiceRow is not null)
        {
            var invoice = await _local.GetInvoiceAsync(_vm.SelectedInvoiceRow.Id, _session);
            if (invoice is not null)
                preselect = await _local.GetCustomerAsync(invoice.CustomerId, _session);
        }

        await vm.LoadAsync(preselect);
        var win = new PaymentWindow(vm) { Owner = this };
        win.Show();
    }

    private async Task OpenYeonsuDeliveryWindowAsync()
    {
        var vm = new YeonsuDeliveryViewModel(_local, _session);
        await vm.InitializeAsync();
        var win = new YeonsuDeliveryWindow(vm, _local, _print, _invoicePrintService, _session)
        {
            Owner = this
        };
        win.Show();
    }

    private async Task OpenEnvironmentSettingsWindowAsync(bool openRecycleBinTab = false)
    {
        var vm = new EnvironmentSettingsViewModel(_local, _session, _api);
        await vm.InitializeAsync();
        var win = new EnvironmentSettingsWindow(vm, openRecycleBinTab)
        {
            Owner = this
        };
        win.ShowDialog();
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private async Task OpenCustomerManagementWindowAsync()
    {
        var vm = new CustomerManagementViewModel(_local, _session);
        await vm.InitializeAsync();
        var win = new CustomerManagementWindow(vm, _local, _session)
        {
            Owner = this
        };
        win.ShowDialog();
        await _vm.RefreshCustomersCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalDashboardWindowAsync()
    {
        var vm = new RentalDashboardViewModel(_rental, _session);
        await vm.LoadAsync();
        var win = new RentalDashboardWindow(vm)
        {
            Owner = this
        };
        win.ShowDialog();
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalBillingWindowAsync()
    {
        var vm = new RentalBillingViewModel(_rental, _local, _session);
        await vm.LoadAsync();
        var win = new RentalBillingWindow(vm)
        {
            Owner = this
        };
        win.ShowDialog();
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalAssetWindowAsync()
    {
        var vm = new RentalAssetViewModel(_rental, _local, _rentalDocuments, _invoicePrintService, _session);
        await vm.LoadAsync();
        var win = new RentalAssetWindow(vm)
        {
            Owner = this
        };
        win.ShowDialog();
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalSettingsWindowAsync()
    {
        var vm = new RentalSettingsViewModel(_rental, _local, _session);
        await vm.LoadAsync();
        var win = new RentalSettingsWindow(vm)
        {
            Owner = this
        };
        win.ShowDialog();
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }
}
