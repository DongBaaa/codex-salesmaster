using System.Windows;
using System.Windows.Input;
using SalesMaster.Desktop.App.ViewModels;

namespace SalesMaster.Desktop.App.Views;

public partial class RentalBillingWindow : Window
{
    public RentalBillingWindow(RentalBillingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12)
            return;

        Close();
        e.Handled = true;
    }

    private async void RegisterSettlementButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RentalBillingViewModel viewModel || viewModel.SelectedRow is null)
        {
            MessageBox.Show("수금을 등록할 대상을 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var paymentViewModel = new PaymentViewModel(viewModel.LocalStateService, viewModel.SessionState);
        await paymentViewModel.LoadAsync();
        await paymentViewModel.ConfigureForRentalBillingAsync(viewModel.SelectedRow.Source);

        var paymentWindow = new PaymentWindow(paymentViewModel)
        {
            Owner = this
        };

        paymentWindow.ShowDialog();
        await viewModel.ReloadCommand.ExecuteAsync(null);
    }
}
