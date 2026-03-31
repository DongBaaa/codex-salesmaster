using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalBillingWindow : Window
{
    private bool _allowClose;
    private bool _closeInProgress;

    public RentalBillingWindow(RentalBillingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += HandleClosing;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogWindowCloseHelper.Close(this);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12)
            return;

        DialogWindowCloseHelper.Close(this);
        e.Handled = true;
    }

    private void StartBillingButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskHelper.Run(this, async () =>
        {
            if (DataContext is not RentalBillingViewModel viewModel)
                return;

            await viewModel.StartBillingCommand.ExecuteAsync(null);
            if (!viewModel.InvoiceToOpenAfterClose.HasValue)
                return;

            _allowClose = true;
            DialogResult = true;
            Close();
        }, "UI", "렌탈 청구 시작", "렌탈 청구 시작 중 오류가 발생했습니다.");
    }

    private void RegisterSettlementButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskHelper.Run(this, async () =>
        {
            if (DataContext is not RentalBillingViewModel viewModel || viewModel.SelectedRow is null)
            {
                MessageBox.Show("수금을 등록할 대상을 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var paymentViewModel = new PaymentViewModel(viewModel.LocalStateService, viewModel.SessionState);
            await paymentViewModel.LoadAsync();
            await paymentViewModel.ConfigureForRentalBillingAsync(
                viewModel.SelectedRow.Source,
                viewModel.SelectedRow.CurrentBillingRunId,
                viewModel.SelectedRow.CurrentBilledAmount,
                viewModel.SelectedRow.CurrentBillingPeriodLabel);

            var paymentWindow = new PaymentWindow(paymentViewModel)
            {
                Owner = this
            };

            paymentWindow.ShowDialog();
            await viewModel.ReloadCommand.ExecuteAsync(null);
        }, "UI", "렌탈 청구 수금 등록", "렌탈 청구 수금 등록 중 오류가 발생했습니다.");
    }

    private async void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _closeInProgress || DataContext is not RentalBillingViewModel viewModel)
            return;

        e.Cancel = true;
        _closeInProgress = true;
        try
        {
            await viewModel.FlushAutoSaveForCloseAsync();
            _allowClose = true;
        }
        catch (OperationCanceledException)
        {
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

    private void NewRentalCustomerButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskHelper.Run(this, async () =>
        {
            if (DataContext is not RentalBillingViewModel viewModel)
                return;

            var onboardingViewModel = new RentalCustomerOnboardingViewModel(
                viewModel.RentalStateService,
                viewModel.LocalStateService,
                viewModel.SessionState);
            await onboardingViewModel.LoadAsync();

            var onboardingWindow = new RentalCustomerOnboardingWindow(onboardingViewModel)
            {
                Owner = this
            };

            onboardingWindow.ShowDialog();
            if (!onboardingViewModel.IsCompleted)
                return;

            await viewModel.ReloadCommand.ExecuteAsync(null);
            if (onboardingViewModel.SavedBillingProfileId.HasValue)
                viewModel.SelectedRow = viewModel.Rows.FirstOrDefault(row => row.Source.Id == onboardingViewModel.SavedBillingProfileId.Value);
        }, "UI", "신규 렌탈 거래처 등록", "신규 렌탈 거래처 등록 중 오류가 발생했습니다.");
    }
}
