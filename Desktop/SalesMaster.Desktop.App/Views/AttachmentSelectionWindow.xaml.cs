using System.Windows;
using SalesMaster.Desktop.App.ViewModels;

namespace SalesMaster.Desktop.App.Views;

public partial class AttachmentSelectionWindow : Window
{
    private readonly AttachmentSelectionDialogViewModel _viewModel;

    public AttachmentSelectionWindow(AttachmentSelectionDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.RequestClose += OnRequestClose;
        Closed += OnClosed;
    }

    private void OnRequestClose(bool? dialogResult)
    {
        DialogResult = dialogResult;
        Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        Closed -= OnClosed;
    }
}
