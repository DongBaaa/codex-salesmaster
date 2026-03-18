using System.Windows;
using System.Windows.Input;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Desktop.App.ViewModels;

namespace SalesMaster.Desktop.App.Views;

public partial class YeonsuDeliveryWindow : Window
{
    private readonly YeonsuDeliveryViewModel _viewModel;
    private readonly LocalStateService _local;
    private readonly StatementPrintService _print;
    private readonly IPrintService _invoicePrintService;
    private readonly SessionState _session;

    public YeonsuDeliveryWindow(
        YeonsuDeliveryViewModel viewModel,
        LocalStateService local,
        StatementPrintService print,
        IPrintService invoicePrintService,
        SessionState session)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _local = local;
        _print = print;
        _invoicePrintService = invoicePrintService;
        _session = session;
        DataContext = viewModel;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void DeliveryRowsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        await OpenSelectedInvoiceAsync();
    }

    private async void OpenInvoiceButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenSelectedInvoiceAsync();
    }

    private async Task OpenSelectedInvoiceAsync()
    {
        if (_viewModel.SelectedDelivery is null)
        {
            MessageBox.Show("열 전표를 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var invoice = await _local.GetInvoiceAsync(_viewModel.SelectedDelivery.InvoiceId, _session);
        if (invoice is null)
        {
            MessageBox.Show("전표를 찾을 수 없습니다.", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var salesViewModel = new SalesViewModel(_local, _print, _invoicePrintService, _session);
        await salesViewModel.LoadAsync();
        salesViewModel.LoadInvoice(invoice);

        var salesWindow = new SalesWindow(salesViewModel)
        {
            Owner = this
        };
        salesWindow.Show();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12)
            return;

        Close();
        e.Handled = true;
    }
}
