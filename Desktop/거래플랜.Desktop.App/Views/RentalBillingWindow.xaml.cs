using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Linq;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalBillingWindow : Window
{
    private readonly EntityEditSessionMonitor? _editSessionMonitor;
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _customerEditorOpenInProgress;
    private readonly HashSet<CustomerEditWindow> _trackedCustomerEditorWindows = new();

    public RentalBillingWindow(RentalBillingViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += HandleClosing;
        Loaded += (_, _) => _editSessionMonitor?.Start();
        Closed += (_, _) => _editSessionMonitor?.Dispose();

        _editSessionMonitor = EntityEditSessionMonitor.TryCreate(
            this,
            "렌탈 청구관리",
            () =>
            {
                if (viewModel.SelectedRow?.IsAggregateRow == true)
                    return null;

                var persistedId = viewModel.SelectedRow?.Source.Id ?? Guid.Empty;
                var entityId = persistedId != Guid.Empty ? persistedId : viewModel.EditId;
                if (entityId == Guid.Empty)
                    return null;

                var displayName = string.IsNullOrWhiteSpace(viewModel.EditCustomerName)
                    ? "렌탈 청구 프로필"
                    : viewModel.EditCustomerName;
                return new EditSessionSubject(
                    "RentalBillingProfile",
                    entityId.ToString("D"),
                    displayName);
            });
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

            if (!viewModel.CanRegisterSettlementSelected)
            {
                MessageBox.Show(
                    "거래처 요약행은 바로 수금등록할 수 없습니다. 개별 청구 프로필 정리 후 다시 시도하세요.",
                    "알림",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
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
            AppLogger.Error("UI", "렌탈 청구관리 창 닫기 전 자동저장 실패", ex);
            var detail = ex.InnerException?.Message ?? ex.Message;
            viewModel.StatusMessage = $"자동저장에 실패했습니다. {detail}";

            var discard = MessageBox.Show(
                this,
                $"자동저장에 실패했습니다.{Environment.NewLine}{detail}{Environment.NewLine}{Environment.NewLine}저장되지 않은 변경사항이 있을 수 있습니다. 저장하지 않고 창을 닫을까요?",
                "렌탈 청구관리",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (discard == MessageBoxResult.Yes)
            {
                _allowClose = true;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    if (IsLoaded)
                        Close();
                }));
            }

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

    private void CustomerLookupButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(this, OpenCustomerLookupAsync, "UI", "렌탈 청구 거래처 조회", "거래처 조회 중 오류가 발생했습니다.");

    private void OpenAssetLinkDialogButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(this, OpenAssetLinkDialogAsync, "UI", "렌탈 자산 연결", "렌탈 자산 연결창을 여는 중 오류가 발생했습니다.");

    private void BillingRowsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
            return;

        if (FindAncestor<DataGridColumnHeader>(source) is not null ||
            FindAncestor<ScrollBar>(source) is not null ||
            FindAncestor<CheckBox>(source) is not null ||
            FindAncestor<Button>(source) is not null ||
            FindAncestor<ComboBox>(source) is not null)
        {
            return;
        }

        if (FindAncestor<DataGridRow>(source) is not DataGridRow dataGridRow ||
            dataGridRow.Item is not RentalBillingViewRow row ||
            DataContext is not RentalBillingViewModel viewModel)
        {
            return;
        }

        viewModel.SelectedRow = row;
        UiTaskHelper.Run(
            this,
            () => OpenCustomerEditorForSelectedRowAsync(row),
            "UI",
            "렌탈 청구 거래처 열기",
            "거래처 등록/수정 창을 여는 중 오류가 발생했습니다.");
    }

    private async Task OpenCustomerLookupAsync()
    {
        if (DataContext is not RentalBillingViewModel viewModel)
            return;

        var dialog = new LookupWindow(
            "거래처 조회",
            await viewModel.BuildCustomerLookupRowsAsync(),
            "거래처 등록",
            async () =>
            {
                var customerVm = new CustomerEditViewModel(viewModel.LocalStateService, viewModel.SessionState);
                await customerVm.LoadAsync();
                var customerWindow = new CustomerEditWindow(customerVm) { Owner = this };
                customerWindow.ShowDialog();
                return await viewModel.BuildCustomerLookupRowsAsync();
            })
        { Owner = this };

        if (dialog.ShowDialog() == true && dialog.SelectedRow?.Tag is LocalCustomer customer)
            viewModel.ApplySelectedCustomer(customer);
    }

    private async Task OpenAssetLinkDialogAsync()
    {
        if (DataContext is not RentalBillingViewModel viewModel)
            return;

        if (viewModel.SelectedRow?.IsAggregateRow == true)
        {
            MessageBox.Show(
                "거래처 요약행에서는 장비 연결을 직접 편집할 수 없습니다. 개별 청구 프로필 정리 후 다시 시도하세요.",
                "렌탈 자산 연결",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(viewModel.EditCustomerName))
        {
            MessageBox.Show("먼저 거래처를 선택하거나 입력하세요.", "렌탈 자산 연결", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dialogViewModel = new RentalAssetLinkDialogViewModel(
            viewModel.RentalStateService,
            viewModel.SessionState,
            viewModel.SelectedRow?.HasPersistedProfile == true ? viewModel.SelectedRow.Source.Id : null,
            viewModel.EditCustomerId,
            viewModel.EditCustomerName,
            viewModel.EditOfficeCode,
            viewModel.EditInstallLocation);
        await dialogViewModel.LoadAsync();

        var dialog = new RentalAssetLinkDialog(dialogViewModel)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
            return;

        viewModel.ApplyAssetLinkSelections(dialogViewModel.GetSelectedAssets());
    }

    private async Task OpenCustomerEditorForSelectedRowAsync(RentalBillingViewRow row)
    {
        if (_customerEditorOpenInProgress)
            return;

        var customerId = row.Source.CustomerId.GetValueOrDefault();
        if (customerId == Guid.Empty)
        {
            MessageBox.Show(
                this,
                "연결된 거래처 식별값이 없어 거래처 등록/수정 창을 열 수 없습니다.",
                "렌탈 청구 거래처 열기",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var existingWindow = Application.Current?.Windows
            .OfType<CustomerEditWindow>()
            .FirstOrDefault(window => window.DataContext is CustomerEditViewModel vm && vm.CustomerId == customerId);
        if (existingWindow is not null)
        {
            AttachCustomerEditorClosedRefresh(existingWindow, row);
            if (existingWindow.WindowState == WindowState.Minimized)
                existingWindow.WindowState = WindowState.Normal;

            existingWindow.Activate();
            existingWindow.Focus();
            return;
        }

        _customerEditorOpenInProgress = true;
        try
        {
            if (DataContext is not RentalBillingViewModel viewModel)
                return;

            var customer = await viewModel.LocalStateService.GetCustomerAsync(customerId, viewModel.SessionState);
            if (customer is null)
            {
                MessageBox.Show(
                    this,
                    "해당 거래처를 찾을 수 없거나 현재 권한으로 열 수 없습니다.",
                    "렌탈 청구 거래처 열기",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var customerVm = new CustomerEditViewModel(viewModel.LocalStateService, viewModel.SessionState);
            await customerVm.LoadAsync(customer);
            var customerWindow = new CustomerEditWindow(customerVm)
            {
                Owner = this
            };
            customerWindow.ShowDialog();
            await viewModel.RefreshSelectedCustomerContextAsync();
            await viewModel.ReloadCommand.ExecuteAsync(null);
            viewModel.SelectedRow = viewModel.Rows.FirstOrDefault(current => current.SelectionId == row.SelectionId)
                                    ?? viewModel.Rows.FirstOrDefault(current => current.Source.Id == row.Source.Id);
        }
        finally
        {
            _customerEditorOpenInProgress = false;
        }
    }

    private void AttachCustomerEditorClosedRefresh(CustomerEditWindow customerWindow, RentalBillingViewRow row)
    {
        if (!_trackedCustomerEditorWindows.Add(customerWindow))
            return;

        void HandleClosed(object? sender, EventArgs args)
        {
            customerWindow.Closed -= HandleClosed;
            _trackedCustomerEditorWindows.Remove(customerWindow);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
            {
                if (DataContext is not RentalBillingViewModel viewModel)
                    return;

                await viewModel.RefreshSelectedCustomerContextAsync();
                await viewModel.ReloadCommand.ExecuteAsync(null);
                viewModel.SelectedRow = viewModel.Rows.FirstOrDefault(current => current.SelectionId == row.SelectionId)
                                        ?? viewModel.Rows.FirstOrDefault(current => current.Source.Id == row.Source.Id);
            }));
        }
        customerWindow.Closed += HandleClosed;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed)
                return typed;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
