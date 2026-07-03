using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class DashboardBalanceDetailsWindow : Window
{
    public DashboardBalanceDetailsWindow(DashboardBalanceDetailViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12 || e.Key == Key.Escape)
        {
            DialogWindowCloseHelper.Close(this);
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => DialogWindowCloseHelper.Close(this);
}
