using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Views;

public partial class CustomerInvoiceLookupWindow : Window
{
    private readonly CustomerInvoiceLookupViewModel _viewModel;
    private readonly Func<InvoiceListRow, Task> _openInvoiceRowAsync;
    private readonly Func<Guid, Task> _openCustomerAsync;
    private readonly Func<VoucherType, LocalCustomer?, Task> _openInvoiceEntryAsync;
    private readonly Func<InvoiceListRow?, LocalCustomer?, Task> _openPaymentEntryAsync;
    private readonly Func<InvoiceListRow?, Task> _printInvoiceRowAsync;

    public CustomerInvoiceLookupWindow(
        CustomerInvoiceLookupViewModel viewModel,
        Func<InvoiceListRow, Task> openInvoiceRowAsync,
        Func<Guid, Task> openCustomerAsync,
        Func<VoucherType, LocalCustomer?, Task> openInvoiceEntryAsync,
        Func<InvoiceListRow?, LocalCustomer?, Task> openPaymentEntryAsync,
        Func<InvoiceListRow?, Task> printInvoiceRowAsync)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _openInvoiceRowAsync = openInvoiceRowAsync;
        _openCustomerAsync = openCustomerAsync;
        _openInvoiceEntryAsync = openInvoiceEntryAsync;
        _openPaymentEntryAsync = openPaymentEntryAsync;
        _printInvoiceRowAsync = printInvoiceRowAsync;
        DataContext = viewModel;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F9)
        {
            PrintSelectedInvoiceRow();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.F12 or Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => DialogWindowCloseHelper.Close(this);

    private void SalesEntryButton_Click(object sender, RoutedEventArgs e)
        => OpenInvoiceEntry(VoucherType.Sales, "판매작성");

    private void PurchaseEntryButton_Click(object sender, RoutedEventArgs e)
        => OpenInvoiceEntry(VoucherType.Purchase, "구매작성");

    private void ProcurementEntryButton_Click(object sender, RoutedEventArgs e)
        => OpenInvoiceEntry(VoucherType.Procurement, "견적/발주");

    private void PaymentEntryButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(
            this,
            OpenSelectedPaymentEntryAndRefreshAsync,
            "UI",
            "거래내역 조회창 수금/지급 입력",
            "수금/지급 입력 창을 여는 중 오류가 발생했습니다.");

    private void PrintStatementButton_Click(object sender, RoutedEventArgs e)
        => PrintSelectedInvoiceRow();

    private void OpenInvoiceEntry(VoucherType voucherType, string actionName)
        => UiTaskHelper.Run(
            this,
            () => OpenInvoiceEntryAndRefreshAsync(voucherType),
            "UI",
            $"거래내역 조회창 {actionName}",
            $"{actionName} 창을 여는 중 오류가 발생했습니다.");

    private void PrintSelectedInvoiceRow()
        => UiTaskHelper.Run(
            this,
            () => _printInvoiceRowAsync(_viewModel.SelectedInvoiceRow),
            "UI",
            "거래내역 조회창 전표 인쇄",
            "전표 인쇄 창을 여는 중 오류가 발생했습니다.");

    private void InvoiceRowsDataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        var source = e.OriginalSource as DependencyObject;
        var row = FindAncestor<DataGridRow>(source);
        if (row?.DataContext is not InvoiceListRow invoiceRow)
            return;

        grid.SelectedItem = invoiceRow;
        row.IsSelected = true;
        _viewModel.SelectedInvoiceRow = invoiceRow;
        row.Focus();
    }

    private void InvoiceRowsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var row = FindAncestor<DataGridRow>(source);
        if (row?.DataContext is not InvoiceListRow invoiceRow)
            return;

        OpenInvoiceRow(invoiceRow);
    }

    private void OpenSelectedInvoiceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedInvoiceRow is { } invoiceRow)
            OpenInvoiceRow(invoiceRow);
    }

    private void OpenInvoiceRow(InvoiceListRow invoiceRow)
        => UiTaskHelper.Run(
            this,
            () => OpenInvoiceRowAndRefreshAsync(invoiceRow),
            "UI",
            "거래내역 조회창 전표/내역 열기",
            "선택한 전표 또는 수금·지급 내역을 여는 중 오류가 발생했습니다.");

    private void CustomerListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var source = e.OriginalSource as DependencyObject;
        var item = FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is LocalCustomer customer)
            listBox.SelectedItem = customer;
    }

    private void CustomerListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var item = FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is not LocalCustomer customer)
            return;

        UiTaskHelper.Run(
            this,
            () => OpenCustomerAndRefreshAsync(customer.Id),
            "UI",
            "거래내역 조회창 거래처 열기",
            "거래처 수정창을 여는 중 오류가 발생했습니다.");
    }

    private Task OpenSelectedPaymentEntryAndRefreshAsync()
        => _openPaymentEntryAsync(_viewModel.SelectedInvoiceRow, _viewModel.ResolveActionCustomer());

    private Task OpenInvoiceEntryAndRefreshAsync(VoucherType voucherType)
        => _openInvoiceEntryAsync(voucherType, _viewModel.ResolveActionCustomer());

    private Task OpenInvoiceRowAndRefreshAsync(InvoiceListRow invoiceRow)
        => _openInvoiceRowAsync(invoiceRow);

    private Task OpenCustomerAndRefreshAsync(Guid customerId)
        => _openCustomerAsync(customerId);

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
}
