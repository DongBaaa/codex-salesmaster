using System.Windows;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

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
        DialogWindowCloseHelper.Close(this, dialogResult);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.RequestClose -= OnRequestClose;
        Closed -= OnClosed;
    }
}
