using System.ComponentModel;
using System.Windows;
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
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"자동저장 후 닫기에 실패했습니다.\n{ex.Message}", "신규 렌탈 거래처 등록", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _closeInProgress = false;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
