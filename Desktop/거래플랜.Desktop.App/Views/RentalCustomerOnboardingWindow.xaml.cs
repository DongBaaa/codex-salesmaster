using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalCustomerOnboardingWindow : Window
{
    private bool _allowClose;
    private bool _closeInProgress;

    public RentalCustomerOnboardingWindow(RentalCustomerOnboardingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Completed += HandleCompleted;
        Closing += HandleClosing;
        Closed += (_, _) => viewModel.Completed -= HandleCompleted;
    }

    private void HandleCompleted(object? sender, EventArgs e)
    {
        _allowClose = true;
        DialogResult = true;
        Close();
    }

    private async void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _closeInProgress || DataContext is not RentalCustomerOnboardingViewModel viewModel)
            return;

        e.Cancel = true;
        _closeInProgress = true;
        try
        {
            await viewModel.FlushAutoSaveAsync();
            _allowClose = true;
        }
        catch (Exception ex)
        {
            _closeInProgress = false;
            viewModel.StatusMessage = $"자동저장 후 창을 닫지 못했습니다. {ex.Message}";
            return;
        }

        _closeInProgress = false;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (!_allowClose || !IsLoaded)
                return;

            Close();
        }));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
