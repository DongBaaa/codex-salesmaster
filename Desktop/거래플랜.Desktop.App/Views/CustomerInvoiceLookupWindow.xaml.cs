using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class CustomerInvoiceLookupWindow : Window
{
    private readonly CustomerInvoiceLookupViewModel _viewModel;
    private readonly Func<InvoiceListRow, Task> _openInvoiceRowAsync;
    private readonly Func<Guid, Task> _openCustomerAsync;

    public CustomerInvoiceLookupWindow(
        CustomerInvoiceLookupViewModel viewModel,
        Func<InvoiceListRow, Task> openInvoiceRowAsync,
        Func<Guid, Task> openCustomerAsync)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _openInvoiceRowAsync = openInvoiceRowAsync;
        _openCustomerAsync = openCustomerAsync;
        DataContext = viewModel;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.F12 or Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => DialogWindowCloseHelper.Close(this);

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
            () => _openInvoiceRowAsync(invoiceRow),
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
            () => _openCustomerAsync(customer.Id),
            "UI",
            "거래내역 조회창 거래처 열기",
            "거래처 수정창을 여는 중 오류가 발생했습니다.");
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
}
