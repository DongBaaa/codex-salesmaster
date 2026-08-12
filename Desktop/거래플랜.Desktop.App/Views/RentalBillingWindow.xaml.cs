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
    private readonly Func<Guid, Window?, Task>? _openInvoiceWindowAsync;
    private readonly Func<Guid, Window?, Task>? _openRentalAssetWindowAsync;
    private readonly Func<Task>? _refreshAfterBillingChangedAsync;
    private bool _allowClose;
    private bool _closeInProgress;
    private bool _customerEditorOpenInProgress;
    private bool _isBillingDetailExpanded;
    private GridLength _billingListColumnWidth = new(2.2, GridUnitType.Star);
    private GridLength _billingDetailColumnWidth = new(1.6, GridUnitType.Star);
    private readonly HashSet<CustomerEditWindow> _trackedCustomerEditorWindows = new();
    private readonly HashSet<RentalAssetWindow> _trackedRentalAssetWindows = new();

    public RentalBillingWindow(
        RentalBillingViewModel viewModel,
        Func<Guid, Window?, Task>? openInvoiceWindowAsync = null,
        Func<Guid, Window?, Task>? openRentalAssetWindowAsync = null,
        Func<Task>? refreshAfterBillingChangedAsync = null)
    {
        InitializeComponent();
        ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(this);
        DataContext = viewModel;
        _openInvoiceWindowAsync = openInvoiceWindowAsync;
        _openRentalAssetWindowAsync = openRentalAssetWindowAsync;
        _refreshAfterBillingChangedAsync = refreshAfterBillingChangedAsync;
        Closing += HandleClosing;
        Loaded += (_, _) => _editSessionMonitor?.Start();
        Closed += (_, _) =>
        {
            UiTaskHelper.Forget(
                () => viewModel.CancelAndDrainPendingBackgroundWorkAsync(),
                "UI",
                "렌탈 청구관리 백그라운드 작업 종료",
                ex => AppLogger.Warn("UI", $"렌탈 청구관리 백그라운드 작업 종료 실패: {ex.Message}"));
            _editSessionMonitor?.Dispose();
        };

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

    private void ToggleBillingDetailWidthButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_isBillingDetailExpanded)
        {
            _billingListColumnWidth = BillingListColumn.Width;
            _billingDetailColumnWidth = BillingDetailColumn.Width;
            BillingListColumn.MinWidth = 0;
            BillingDetailColumn.MinWidth = 0;
            BillingListColumn.Width = new GridLength(0);
            BillingWorkspaceSplitterColumn.Width = new GridLength(0);
            BillingWorkspaceGridSplitter.Visibility = Visibility.Collapsed;
            BillingDetailColumn.Width = new GridLength(1, GridUnitType.Star);
            ToggleBillingDetailWidthButton.Content = "목록 같이 보기";
            _isBillingDetailExpanded = true;
            return;
        }

        BillingListColumn.MinWidth = 420;
        BillingDetailColumn.MinWidth = 620;
        BillingListColumn.Width = _billingListColumnWidth;
        BillingWorkspaceSplitterColumn.Width = new GridLength(10);
        BillingWorkspaceGridSplitter.Visibility = Visibility.Visible;
        BillingDetailColumn.Width = _billingDetailColumnWidth;
        ToggleBillingDetailWidthButton.Content = "상세 크게 보기";
        _isBillingDetailExpanded = false;
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

            var assetCoverageWarning = viewModel.GetBillingAssetCoverageStartWarning();
            if (!string.IsNullOrWhiteSpace(assetCoverageWarning))
            {
                var confirmCoverage = MessageBox.Show(
                    this,
                    $"{assetCoverageWarning}{Environment.NewLine}{Environment.NewLine}이 상태로 조회/작성 기준일의 청구서를 만들까요?",
                    "청구 대상 장비 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirmCoverage != MessageBoxResult.Yes)
                    return;
            }

            if (viewModel.SelectedRow?.HasPastUnresolved == true)
            {
                var confirm = MessageBox.Show(
                    this,
                    $"{viewModel.SelectedRow.CustomerDisplayName} 거래처에 이전 청구 미처리 내역 {viewModel.SelectedRow.PastUnresolvedCount:N0}건 / 미수 {viewModel.SelectedRow.PastUnresolvedAmount:N0}원이 있습니다.{Environment.NewLine}{Environment.NewLine}그래도 조회/작성 기준일의 청구서를 만들까요?",
                    "과거 미처리 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            if (viewModel.SelectedRow?.IsAggregateRow == true)
            {
                var row = viewModel.SelectedRow;
                var profileCount = row.GroupedPersistedProfileIds
                    .Where(id => id != Guid.Empty)
                    .Distinct()
                    .Count();
                var excludedUnlinkedText = row.GroupedUnlinkedAssetCount > 0
                    ? $"{Environment.NewLine}청구설정 필요 장비 {row.GroupedUnlinkedAssetCount:N0}대는 제외됩니다."
                    : string.Empty;
                var confirm = MessageBox.Show(
                    this,
                    $"{row.CustomerDisplayName} 거래처별 요약에 포함된 개별 청구 프로필 {profileCount:N0}건을 조회/작성 기준일로 청구 시작하시겠습니까?{excludedUnlinkedText}{Environment.NewLine}{Environment.NewLine}일부 프로필에서 실패하면 성공/실패 건수가 나뉘어 표시됩니다.",
                    "거래처별 요약 청구 확인",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            await viewModel.StartBillingCommand.ExecuteAsync(null);
            var invoiceId = viewModel.ConsumeInvoiceToOpenAfterClose();
            var billingCreated = viewModel.ConsumeBillingCreatedSinceLastConsume();
            if (billingCreated && _refreshAfterBillingChangedAsync is not null)
                await _refreshAfterBillingChangedAsync();

            if (invoiceId.HasValue && _openInvoiceWindowAsync is not null)
                await _openInvoiceWindowAsync(invoiceId.Value, this);
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
                    "거래처별 요약행에서는 바로 수금등록할 수 없습니다. 거래처 행을 펼쳐 실제 청구건을 선택한 뒤 다시 시도하세요.",
                    "알림",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            await OpenRentalSettlementWindowAsync(viewModel, viewModel.SelectedBillingHistory);
        }, "UI", "렌탈 청구 수금 등록", "렌탈 청구 수금 등록 중 오류가 발생했습니다.");
    }

    private void BillingHistoryRegisterButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskHelper.Run(this, async () =>
        {
            if (DataContext is not RentalBillingViewModel viewModel ||
                sender is not FrameworkElement element ||
                element.DataContext is not RentalBillingHistoryRow history)
            {
                MessageBox.Show("입금 등록할 청구/입금 내역을 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            viewModel.SelectedBillingHistory = history;
            await OpenRentalSettlementWindowAsync(viewModel, history);
        }, "UI", "렌탈 청구 월별 입금 등록", "렌탈 청구 월별 입금 등록 중 오류가 발생했습니다.");
    }

    private void BillingHistoryDataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dataGrid || e.OriginalSource is not DependencyObject source)
            return;

        var dataGridRow = FindAncestor<DataGridRow>(source);
        if (dataGridRow?.Item is not RentalBillingHistoryRow historyRow)
            return;

        dataGridRow.IsSelected = true;
        dataGrid.SelectedItem = historyRow;
    }

    private void IncludedAssetsDataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid dataGrid || e.OriginalSource is not DependencyObject source)
            return;

        var dataGridRow = FindAncestor<DataGridRow>(source);
        if (dataGridRow?.Item is not RentalBillingAssetOption includedAsset)
            return;

        dataGridRow.IsSelected = true;
        dataGrid.SelectedItem = includedAsset;
        if (DataContext is RentalBillingViewModel viewModel)
            viewModel.SelectedIncludedAsset = includedAsset;
    }

    private void IncludedAssetsDataGrid_BeginningEdit(object sender, DataGridBeginningEditEventArgs e)
    {
        if (e.Row.Item is not RentalBillingAssetOption includedAsset ||
            !includedAsset.IsReferenceOnly)
        {
            return;
        }

        e.Cancel = true;
        if (DataContext is not RentalBillingViewModel viewModel)
            return;

        var assetLabel = string.IsNullOrWhiteSpace(includedAsset.ManagementNumber)
            ? includedAsset.ItemName
            : includedAsset.ManagementNumber;
        var labelPrefix = string.IsNullOrWhiteSpace(assetLabel)
            ? "선택한 자산은"
            : $"'{assetLabel}' 자산은";
        viewModel.StatusMessage =
            $"{labelPrefix} 다른 업체의 참조 전용 자산이므로 원본 정보를 수정할 수 없습니다. " +
            "대표자산은 행을 선택한 뒤 '대표자산 지정' 버튼으로 설정할 수 있습니다.";
    }

    private void OpenIncludedAssetInRentalAssetWindowMenuItem_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(
            this,
            OpenSelectedIncludedAssetInRentalAssetWindowAsync,
            "UI",
            "거래처 임대 자산 설치현황 열기",
            "선택한 거래처 임대 자산의 렌탈 자산/설치현황 창을 여는 중 오류가 발생했습니다.");

    private async Task OpenSelectedIncludedAssetInRentalAssetWindowAsync()
    {
        if (DataContext is not RentalBillingViewModel viewModel ||
            viewModel.SelectedIncludedAsset is not { AssetId: var assetId } ||
            assetId == Guid.Empty)
        {
            MessageBox.Show(
                this,
                "렌탈 자산/설치현황에서 열 거래처 임대 자산을 먼저 선택하세요.",
                "렌탈 자산/설치현황",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var existingTargetWindow = Application.Current?.Windows
            .OfType<RentalAssetWindow>()
            .FirstOrDefault(window => window.DataContext is RentalAssetViewModel rentalAssetViewModel &&
                                      rentalAssetViewModel.SelectedRow?.Source.Id == assetId);
        if (existingTargetWindow is not null)
        {
            AttachRentalAssetEditorClosedRefresh(existingTargetWindow, assetId);
            if (existingTargetWindow.WindowState == WindowState.Minimized)
                existingTargetWindow.WindowState = WindowState.Normal;

            existingTargetWindow.Activate();
            existingTargetWindow.Focus();
            viewModel.StatusMessage = "이미 열려 있는 렌탈 자산/설치현황 창으로 이동했습니다.";
            return;
        }

        if (_openRentalAssetWindowAsync is not null)
        {
            await _openRentalAssetWindowAsync(assetId, this);
            viewModel.StatusMessage = "선택한 거래처 임대 자산을 렌탈 자산/설치현황 창에서 열었습니다.";
            return;
        }

        var mainWindow = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();
        if (mainWindow is null)
        {
            MessageBox.Show(
                this,
                "메인 창 정보를 찾지 못해 렌탈 자산/설치현황 창을 열 수 없습니다.",
                "렌탈 자산/설치현황",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var rentalAssetViewModel = new RentalAssetViewModel(
            mainWindow.RentalStateService,
            mainWindow.LocalStateService,
            mainWindow.RentalDocumentService,
            mainWindow.InvoicePrintService,
            mainWindow.SessionState);
        var rentalAssetWindow = new RentalAssetWindow(rentalAssetViewModel)
        {
            Owner = this
        };

        WindowShowHelper.ShowModelessWithDeferredLoad(
            rentalAssetWindow,
            () => rentalAssetViewModel.LoadAndSelectAssetAsync(assetId),
            "렌탈 자산 / 설치현황",
            "선택한 거래처 임대 자산 정보를 불러오지 못했습니다.",
            this,
            () => viewModel.RefreshAfterExternalAssetEditAsync(assetId));
        viewModel.StatusMessage = "선택한 거래처 임대 자산을 렌탈 자산/설치현황 창에서 여는 중입니다.";
    }

    private void BillingHistoryDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
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
            dataGridRow.Item is not RentalBillingHistoryRow history ||
            DataContext is not RentalBillingViewModel viewModel)
        {
            return;
        }

        viewModel.SelectedBillingHistory = history;
        UiTaskHelper.Run(
            this,
            () => OpenBillingHistoryInvoiceAsync(history),
            "UI",
            "렌탈 청구 연결 전표 열기",
            "렌탈 청구/입금 내역의 연결 전표를 여는 중 오류가 발생했습니다.");
    }

    private async Task OpenBillingHistoryInvoiceAsync(RentalBillingHistoryRow history)
    {
        if (history.InvoiceId is not Guid invoiceId || invoiceId == Guid.Empty)
        {
            MessageBox.Show(
                this,
                "선택한 청구/입금 내역에 연결된 전표가 없습니다.",
                "렌탈 청구 연결 전표",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (_openInvoiceWindowAsync is null)
        {
            MessageBox.Show(
                this,
                "전표 창 열기 경로가 연결되지 않아 전표를 열 수 없습니다.",
                "렌탈 청구 연결 전표",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await _openInvoiceWindowAsync(invoiceId, this);
    }

    private async Task OpenRentalSettlementWindowAsync(RentalBillingViewModel viewModel, RentalBillingHistoryRow? history)
    {
        if (viewModel.SelectedRow is null)
        {
            MessageBox.Show("입금을 등록할 대상을 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!viewModel.CanRegisterSettlementSelected)
        {
            MessageBox.Show(
                "거래처별 요약행에서는 바로 입금등록할 수 없습니다. 거래처 행을 펼쳐 실제 청구건을 선택한 뒤 다시 시도하세요.",
                "알림",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (history is not null)
        {
            if (!history.CanRegisterSettlement)
            {
                MessageBox.Show("선택한 조회/작성 기준일의 청구는 남은 미수금이 없어 입금 등록할 금액이 없습니다.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (history.BillingProfileId != viewModel.SelectedRow.Source.Id)
            {
                MessageBox.Show(
                    "거래처별 요약에 포함된 다른 청구건입니다. 거래처 행을 펼쳐 실제 청구건을 선택한 뒤 입금 등록하세요.",
                    "알림",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
        }

        var billingRunId = history is not null && history.BillingRunId != Guid.Empty
            ? history.BillingRunId
            : viewModel.SelectedRow.CurrentBillingRunId;
        var billedAmount = history is not null && history.BilledAmount > 0m
            ? history.BilledAmount
            : viewModel.SelectedRow.CurrentBilledAmount;
        var periodLabel = !string.IsNullOrWhiteSpace(history?.PeriodLabel)
            ? history!.PeriodLabel
            : viewModel.SelectedRow.CurrentBillingPeriodLabel;

        var paymentViewModel = new PaymentViewModel(viewModel.LocalStateService, viewModel.SessionState);
        await paymentViewModel.LoadAsync();
        await paymentViewModel.ConfigureForRentalBillingAsync(
            viewModel.SelectedRow.Source,
            billingRunId,
            billedAmount,
            periodLabel);

        var paymentWindow = new PaymentWindow(paymentViewModel)
        {
            Owner = this
        };

        paymentWindow.Closed += (_, _) => UiTaskHelper.Run(
            this,
            () => viewModel.ReloadCommand.ExecuteAsync(null),
            "UI",
            "렌탈 수금 입력 후 청구관리 새로고침",
            "수금 입력 후 렌탈 청구관리 내역을 다시 불러오는 중 오류가 발생했습니다.");
        WindowShowHelper.ShowModeless(paymentWindow);
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

            onboardingWindow.Closed += (_, _) => UiTaskHelper.Run(
                this,
                async () =>
                {
                    if (!onboardingViewModel.IsCompleted)
                        return;

                    await viewModel.ReloadCommand.ExecuteAsync(null);
                    if (onboardingViewModel.SavedBillingProfileId.HasValue)
                        viewModel.SelectedRow = viewModel.Rows.FirstOrDefault(row => row.Source.Id == onboardingViewModel.SavedBillingProfileId.Value);
                },
                "UI",
                "신규 렌탈 거래처 등록 후 청구관리 새로고침",
                "신규 렌탈 거래처 등록 후 청구관리 목록을 다시 불러오는 중 오류가 발생했습니다.");
            WindowShowHelper.ShowModeless(onboardingWindow);
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
                DialogWindowCloseHelper.ShowDialog(customerWindow);
                return await viewModel.BuildCustomerLookupRowsAsync();
            })
        { Owner = this };

        if (DialogWindowCloseHelper.ShowDialog(dialog) == true && dialog.SelectedRow?.Tag is LocalCustomer customer)
            viewModel.ApplySelectedCustomer(customer);
    }

    private async Task OpenAssetLinkDialogAsync()
    {
        if (DataContext is not RentalBillingViewModel viewModel)
            return;

        if (!viewModel.CanEditBillingProfileDetails)
        {
            MessageBox.Show(
                "거래처별 요약행에서는 장비 연결을 직접 편집할 수 없습니다. 거래처 행을 펼쳐 실제 청구건을 선택한 뒤 다시 시도하세요.",
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

        if (DialogWindowCloseHelper.ShowDialog(dialog) != true)
            return;

        var selectedAssets = dialogViewModel.GetSelectedAssets();
        if (!await viewModel.ApplyAssetLinkSelectionsAndSaveAsync(selectedAssets))
        {
            MessageBox.Show(
                this,
                $"{viewModel.StatusMessage}\n\n입력한 청구관리 내용은 유지됩니다. 내용을 확인한 뒤 다시 적용해 주세요.",
                "렌탈 자산 적용 실패",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
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
            AttachCustomerEditorClosedRefresh(customerWindow, row);
            WindowShowHelper.ShowModeless(customerWindow);
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
            if (Application.Current?.MainWindow is MainWindow { IsShutdownProtectionActive: true })
                return;

            UiTaskHelper.Forget(
                async () =>
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (DataContext is not RentalBillingViewModel viewModel)
                        return;

                    await viewModel.RefreshSelectedCustomerContextAsync();
                    await viewModel.ReloadCommand.ExecuteAsync(null);
                    viewModel.SelectedRow = viewModel.Rows.FirstOrDefault(current => current.SelectionId == row.SelectionId)
                                            ?? viewModel.Rows.FirstOrDefault(current => current.Source.Id == row.Source.Id);
                },
                "UI",
                "렌탈 청구 거래처 편집 후 새로고침",
                ex => AppLogger.Warn("UI", $"렌탈 청구 거래처 편집 후 새로고침 실패: {ex.Message}"));
        }
        customerWindow.Closed += HandleClosed;
    }

    private void AttachRentalAssetEditorClosedRefresh(RentalAssetWindow rentalAssetWindow, Guid assetId)
    {
        if (!_trackedRentalAssetWindows.Add(rentalAssetWindow))
            return;

        void HandleClosed(object? sender, EventArgs args)
        {
            rentalAssetWindow.Closed -= HandleClosed;
            _trackedRentalAssetWindows.Remove(rentalAssetWindow);
            if (Application.Current?.MainWindow is MainWindow { IsShutdownProtectionActive: true })
                return;

            UiTaskHelper.Forget(
                async () =>
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (DataContext is RentalBillingViewModel viewModel)
                        await viewModel.RefreshAfterExternalAssetEditAsync(assetId);
                },
                "UI",
                "렌탈 자산 편집 후 청구관리 새로고침",
                ex => AppLogger.Warn("UI", $"렌탈 자산 편집 후 청구관리 새로고침 실패: {ex.Message}"));
        }

        rentalAssetWindow.Closed += HandleClosed;
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
