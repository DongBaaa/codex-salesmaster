using System.Windows;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class PrintPreviewWindow : Window
{
    private readonly PrintPreviewViewModel _viewModel;

    public PrintPreviewWindow(PrintPreviewViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;
        Closed += OnClosed;
    }

    private void OnRequestClose()
    {
        DialogResult = _viewModel.WasPrinted;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        Closed -= OnClosed;
    }
}
