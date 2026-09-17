using System.Windows;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class PrintPreviewWindow : Window
{
    private readonly PrintPreviewViewModel _viewModel;
    private IDisposable? _authorizationMonitor;

    public PrintPreviewWindow(PrintPreviewViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        PreviewPrintCommandRouting.Bind(this, PreviewDocumentViewer, _viewModel.PrintCommand);
        _viewModel.CurrentPageNumberProvider = () => PreviewDocumentViewer?.MasterPageNumber;
        _viewModel.RequestClose += OnRequestClose;
        Closed += OnClosed;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _authorizationMonitor ??= _viewModel.MonitorAuthorization();
    }

    private void OnRequestClose()
    {
        DialogWindowCloseHelper.Close(this, _viewModel.WasPrinted);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _authorizationMonitor?.Dispose();
        _authorizationMonitor = null;
        Loaded -= OnLoaded;
        _viewModel.RequestClose -= OnRequestClose;
        _viewModel.CurrentPageNumberProvider = null;
        Closed -= OnClosed;
    }
}
