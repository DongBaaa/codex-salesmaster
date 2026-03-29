using System.Windows;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalCustomerOnboardingWindow : Window
{
    public RentalCustomerOnboardingWindow(RentalCustomerOnboardingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Completed += HandleCompleted;
        Closed += (_, _) => viewModel.Completed -= HandleCompleted;
    }

    private void HandleCompleted(object? sender, EventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
