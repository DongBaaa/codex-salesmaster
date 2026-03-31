using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Views;

namespace 거래플랜.Desktop.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly LocalStateService _local;
    private readonly RentalStateService _rental;
    private readonly RentalDocumentService _rentalDocuments;
    private readonly StatementPrintService _print;
    private readonly IPrintService _invoicePrintService;
    private readonly SessionState _session;
    private readonly ErpApiClient _api;
    private readonly SyncService _sync;
    private readonly BackupService _backup;
    private readonly SyncDiagnosticsService _diagnostics;
    private readonly DesktopAppUpdateService _updateService;
    private readonly DispatcherTimer _centralRevisionPollTimer;
    private bool _isInitialized;
    private DateTime _lastCentralRefreshUtc = DateTime.MinValue;
    private long _lastPassiveServerRevisionHint;
    private bool _centralRefreshInProgress;
    private bool _deactivateFlushInProgress;
    private bool _updatePromptInProgress;
    private bool _isClosingOrClosed;

    public MainWindow(MainViewModel vm, LocalStateService local,
                      RentalStateService rental,
                      RentalDocumentService rentalDocuments,
                      StatementPrintService print,
                      IPrintService invoicePrintService,
                      SessionState session,
                      ErpApiClient api,
                      SyncService sync,
                      BackupService backup,
                      SyncDiagnosticsService diagnostics)
    {
        InitializeComponent();
        _vm = vm;
        _local = local;
        _rental = rental;
        _rentalDocuments = rentalDocuments;
        _print = print;
        _invoicePrintService = invoicePrintService;
        _session = session;
        _api = api;
        _sync = sync;
        _backup = backup;
        _diagnostics = diagnostics;
        _updateService = new DesktopAppUpdateService(api);
        DataContext = vm;
        Activated += MainWindow_Activated;
        Deactivated += MainWindow_Deactivated;
        Closed += (_, _) => BeginShutdownProtection();
        _centralRevisionPollTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _centralRevisionPollTimer.Tick += CentralRevisionPollTimer_Tick;
    }

    public void BeginShutdownProtection()
    {
        if (_isClosingOrClosed)
            return;

        _isClosingOrClosed = true;
        _centralRevisionPollTimer?.Stop();
    }

    public void EndShutdownProtection()
    {
        _isClosingOrClosed = false;
        if (_isInitialized && !_session.IsOfflineMode)
            _centralRevisionPollTimer?.Start();
    }

    private void RunUiAsync(Func<Task> operation, string operationName, string? userMessage = null)
        => UiTaskHelper.Run(
            this,
            operation,
            "UI",
            operationName,
            userMessage ?? $"{operationName} 중 오류가 발생했습니다.");

    public async Task InitAsync()
    {
        if (_isClosingOrClosed)
            return;

        _vm.SetInvoiceDefaultDateRange(await ResolveServerTodayAsync());
        await _vm.LoadAsync();
        if (!_session.IsOfflineMode)
        {
            _sync.Start(TimeSpan.FromMinutes(5));
            _centralRevisionPollTimer.Start();
        }

        var popupSections = new List<string>();
        if (!string.IsNullOrWhiteSpace(_vm.ContractAlertPopupMessage))
            popupSections.Add(_vm.ContractAlertPopupMessage);
        if (!string.IsNullOrWhiteSpace(_vm.RentalAlertPopupMessage))
            popupSections.Add(_vm.RentalAlertPopupMessage);

        if (popupSections.Count > 0)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine + Environment.NewLine, popupSections)
                + Environment.NewLine
                + Environment.NewLine
                + "확인을 누르면 메인화면으로 이동해 계속 작업할 수 있습니다.",
                "대시보드 알림",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        await CheckAndPromptForDesktopUpdateAsync();

        _isInitialized = true;
    }

    private async Task<DateOnly> ResolveServerTodayAsync()
    {
        if (_session.IsOfflineMode)
            return DateOnly.FromDateTime(DateTime.Today);

        try
        {
            var syncStatus = await _api.GetSyncStatusAsync();
            if (syncStatus is null)
                return DateOnly.FromDateTime(DateTime.Today);

            var serverUtc = syncStatus.ServerUtc.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(syncStatus.ServerUtc, DateTimeKind.Utc)
                : syncStatus.ServerUtc.ToUniversalTime();
            var serverLocal = TimeZoneInfo.ConvertTimeFromUtc(serverUtc, TimeZoneInfo.Local);
            return DateOnly.FromDateTime(serverLocal);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MAIN", $"서버 기준 날짜 조회 실패: {ex.Message}");
            return DateOnly.FromDateTime(DateTime.Today);
        }
    }

    private void MainWindow_Activated(object? sender, EventArgs e)
        => RunUiAsync(
            () => MainWindow_ActivatedAsync(),
            "메인 창 활성화 처리",
            "창 활성화 처리 중 오류가 발생했습니다.");

    private async Task MainWindow_ActivatedAsync()
    {
        if (_isClosingOrClosed || !_isInitialized || _session.IsOfflineMode)
            return;

        await RunPassiveSyncRefreshAsync("창 활성화", TimeSpan.FromMinutes(1), requireServerRevisionChange: true);
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
        => RunUiAsync(
            () => MainWindow_DeactivatedAsync(),
            "메인 창 비활성화 처리",
            "창 비활성화 처리 중 오류가 발생했습니다.");

    private async Task MainWindow_DeactivatedAsync()
    {
        if (_isClosingOrClosed || !_isInitialized || _session.IsOfflineMode || _deactivateFlushInProgress)
            return;

        _deactivateFlushInProgress = true;
        try
        {
            await FlushPendingChangesBeforeNavigationAsync("창 비활성화", blockUntilServerFlush: false);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SYNC", $"Window deactivation flush failed: {ex.Message}");
        }
        finally
        {
            _deactivateFlushInProgress = false;
        }
    }

    private async Task PollCentralRevisionAsync()
    {
        if (_isClosingOrClosed || !_isInitialized || _session.IsOfflineMode)
            return;

        await RunPassiveSyncRefreshAsync("중앙 revision polling", TimeSpan.FromMinutes(2), requireServerRevisionChange: true);
    }

    private async Task RunPassiveSyncRefreshAsync(string reason, TimeSpan minInterval, bool requireServerRevisionChange)
    {
        if (_isClosingOrClosed || !_isInitialized || _session.IsOfflineMode || _centralRefreshInProgress || _vm.ForceSyncCommand.IsRunning)
            return;

        _centralRefreshInProgress = true;
        try
        {
            var pendingServerRevision = await GetPendingPassiveServerRevisionAsync(minInterval, requireServerRevisionChange);
            if (!pendingServerRevision.HasValue)
                return;

            var syncOk = await _sync.TrySyncAsync();
            if (!syncOk)
                return;

            _lastCentralRefreshUtc = DateTime.UtcNow;
            if (pendingServerRevision.Value > 0)
            {
                var lastSyncRevisionRaw = await _local.GetSettingAsync("LastSyncRevision");
                _ = long.TryParse(lastSyncRevisionRaw, out var lastSyncRevision);
                _lastPassiveServerRevisionHint = Math.Max(_lastPassiveServerRevisionHint, Math.Max(pendingServerRevision.Value, lastSyncRevision));
            }

            await _vm.ReloadAfterPassiveSyncAsync();
            AppLogger.Info("SYNC", $"{reason} 후 경량 재동기화 완료");
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SYNC", $"{reason} refresh failed: {ex.Message}");
        }
        finally
        {
            _centralRefreshInProgress = false;
        }
    }

    private async Task<long?> GetPendingPassiveServerRevisionAsync(TimeSpan minInterval, bool requireServerRevisionChange)
    {
        if (_sync.HasActiveOrQueuedSync)
            return null;

        var nowUtc = DateTime.UtcNow;
        if (nowUtc - _lastCentralRefreshUtc < minInterval)
            return null;

        if (_sync.HasRecentSuccessfulSync(minInterval))
            return null;

        if (await _local.HasPendingSyncChangesAsync())
            return 0L;

        if (!requireServerRevisionChange)
            return 0L;

        var status = await _api.GetSyncStatusAsync();
        if (status is null)
            return null;

        var lastSyncRevisionRaw = await _local.GetSettingAsync("LastSyncRevision");
        _ = long.TryParse(lastSyncRevisionRaw, out var lastSyncRevision);
        var baselineRevision = Math.Max(lastSyncRevision, _lastPassiveServerRevisionHint);
        return status.CurrentServerRevision > baselineRevision
            ? status.CurrentServerRevision
            : null;
    }

    // F9: 거래명세서 인쇄, F6: 신규 판매작성
    // Ctrl+Shift+C: 거래처등록, Ctrl+Shift+I: 재고관리, Ctrl+Shift+P: 수금지불
    private void Window_KeyDown(object sender, KeyEventArgs e)
        => RunUiAsync(
            () => Window_KeyDownAsync(e),
            "메인 단축키 처리",
            "단축키 처리 중 오류가 발생했습니다.");

    private async Task Window_KeyDownAsync(KeyEventArgs e)
    {
        if (e.Key == Key.F9)
        {
            if (_vm.PrintStatementCommand.CanExecute(null))
                _vm.PrintStatementCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F6)
        {
            await OpenSalesWindowAsync(preselectSelectedCustomer: true);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.C)
            {
                await OpenCustomerEditorAsync();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.I)
            {
                await OpenInventoryWindowAsync();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.P)
            {
                await OpenPaymentPopupAsync();
                e.Handled = true;
            }
        }
    }

    // 판매작성 (리스트 툴바 버튼)
    private void SalesToolbarButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenSalesWindowAsync(preselectSelectedCustomer: true), "판매 전표 창 열기");

    private void PurchaseToolbarButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenPurchaseWindowAsync(preselectSelectedCustomer: true), "매입 전표 창 열기");

    private void ProcurementToolbarButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenProcurementWindowAsync(preselectSelectedCustomer: true), "발주 전표 창 열기");

    // 전표 목록 더블클릭 수정
    private void InvoiceRowsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => RunUiAsync(OpenSelectedInvoiceEditorAsync, "전표 상세 열기");

    private async Task OpenSelectedInvoiceEditorAsync()
    {
        if (_vm.SelectedInvoiceRow is null)
        {
            MessageBox.Show("수정할 전표를 선택하세요.", "알림", MessageBoxButton.OK);
            return;
        }

        var inv = await _local.GetInvoiceAsync(_vm.SelectedInvoiceRow.Id, _session);
        if (inv is null) return;

        await OpenInvoiceWindowAsync(inv);
    }

    // 거래처 우클릭 -> 거래처 수정
    private void CustomerEditContextMenu_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(CustomerEditContextMenu_ClickAsync, "거래처 수정 창 열기");

    private async Task CustomerEditContextMenu_ClickAsync()
    {
        var customer = _vm.SelectedCustomerFilter;
        if (customer is null) return;
        await OpenCustomerEditorAsync(customer);
    }

    // 거래처 우클릭 -> 거래처 삭제
    private void CustomerDeleteContextMenu_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(DeleteSelectedCustomerAsync, "거래처 삭제");

    // 거래처 더블클릭 -> 거래처 수정창 열기
    private void CustomerListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => RunUiAsync(CustomerListBox_MouseDoubleClickAsync, "거래처 상세 열기");

    private async Task CustomerListBox_MouseDoubleClickAsync()
    {
        var customer = _vm.SelectedCustomerFilter;
        if (customer is null)
            return;

        await OpenCustomerEditorAsync(customer);
    }

    private void CustomerListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
            return;

        var source = e.OriginalSource as DependencyObject;
        var item = FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is Data.LocalCustomer customer)
            listBox.SelectedItem = customer;
    }

    private async Task DeleteSelectedCustomerAsync()
    {
        var customer = _vm.SelectedCustomerFilter;
        if (customer is null)
        {
            MessageBox.Show("삭제할 거래처를 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var activeInvoices = await _local.GetInvoicesAsync(customerId: customer.Id);
        if (activeInvoices.Count > 0)
        {
            MessageBox.Show(
                $"해당 거래처 전표가 {activeInvoices.Count:N0}건 남아 있어 삭제할 수 없습니다.\n먼저 전표를 모두 삭제한 뒤 거래처를 삭제하세요.",
                "거래처 삭제",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"거래처 '{customer.NameOriginal}'를 삭제하시겠습니까?{Environment.NewLine}삭제된 항목은 환경설정 > 휴지통에서 복원할 수 있습니다.",
            "거래처 삭제 확인",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
            return;

        var deleteCustomerResult = await _local.DeleteCustomerAsync(customer.Id, _session);
        if (!deleteCustomerResult.Success)
        {
            MessageBox.Show(deleteCustomerResult.Message, "거래처 삭제", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await _vm.RefreshCustomersCommand.ExecuteAsync(null);
    }

    // 재고관리 버튼
    private void InventoryButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenInventoryWindowAsync, "재고관리 창 열기");

    // 거래처등록 버튼
    private void CustomerEditButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenCustomerEditorAsync(), "거래처 등록 창 열기");

    // 거래처삭제 버튼
    private void CustomerDeleteButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(DeleteSelectedCustomerAsync, "거래처 삭제");

    private void CustomerManagementButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenCustomerManagementWindowAsync, "거래처관리 창 열기");

    private void CustomerSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
            return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void CustomerRegisterMenuItem_Click(object sender, RoutedEventArgs e)
        => CustomerEditButton_Click(sender, e);

    private void CustomerDeleteMenuItem_Click(object sender, RoutedEventArgs e)
        => CustomerDeleteButton_Click(sender, e);

    private void CustomerManagementMenuItem_Click(object sender, RoutedEventArgs e)
        => CustomerManagementButton_Click(sender, e);

    private void NewRentalCustomerButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenRentalCustomerOnboardingAsync, "신규 렌탈 거래처 등록");

    private void DeleteSelectedInvoicesContextMenu_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(
            () => DeleteSelectedInvoicesContextMenu_ClickAsync(sender),
            "선택 전표 삭제",
            "전표를 삭제하는 중 오류가 발생했습니다.");

    private async Task DeleteSelectedInvoicesContextMenu_ClickAsync(object sender)
    {
        var rows = GetSelectedInvoiceRows(sender).ToList();
        if (rows.Count == 0)
        {
            MessageBox.Show("삭제할 전표를 선택하세요.", "알림", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            $"선택한 전표를 삭제하시겠습니까?{Environment.NewLine}삭제된 전표는 환경설정 > 휴지통에서 복원할 수 있습니다.",
            "전표 삭제 확인",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
            return;

        foreach (var rowId in rows.Select(r => r.Id).Distinct())
        {
            var deleteInvoiceResult = await _local.DeleteInvoiceAsync(rowId, _session);
            if (!deleteInvoiceResult.Success)
            {
                MessageBox.Show(deleteInvoiceResult.Message, "전표 삭제", MessageBoxButton.OK, MessageBoxImage.Warning);
                break;
            }
        }

        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private static IEnumerable<InvoiceListRow> GetSelectedInvoiceRows(object sender)
    {
        if (sender is not MenuItem menuItem)
            return Enumerable.Empty<InvoiceListRow>();

        if (menuItem.Parent is not ContextMenu contextMenu)
            return Enumerable.Empty<InvoiceListRow>();

        if (contextMenu.PlacementTarget is not DataGrid grid)
            return Enumerable.Empty<InvoiceListRow>();

        return grid.SelectedItems.OfType<InvoiceListRow>();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T found)
                return found;
            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    // 판매작성 버튼(헤더)
    private void SalesButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenSalesWindowAsync(preselectSelectedCustomer: false), "판매 전표 창 열기");

    // 수금지불 버튼(헤더)
    private void PaymentButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenPaymentPopupAsync, "수금/지급 창 열기");

    // 자료기간별 집계 버튼(헤더)
    private void PeriodLedgerButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenPeriodLedgerWindowAsync, "자료기간별 집계 창 열기");

    private async Task OpenPeriodLedgerWindowAsync()
    {
        var vm = new PeriodLedgerViewModel(
            _local,
            new PeriodLedgerAggregationService(_local),
            new PeriodLedgerExcelExportService(),
            _session);

        await vm.InitializeAsync();
        var win = new PeriodLedgerWindow(vm) { Owner = this };
        win.ShowDialog();
    }

    private void YeonsuDeliveryButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenYeonsuDeliveryWindowAsync, "연수구 납품 창 열기");

    private void EnvironmentSettingsButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenEnvironmentSettingsWindowAsync(), "환경설정 창 열기");

    private void RecycleBinButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(() => OpenEnvironmentSettingsWindowAsync(EnvironmentSettingsInitialTab.RecycleBin), "휴지통 창 열기");

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(LogoutAsync, "로그아웃", "로그아웃 처리 중 오류가 발생했습니다.");

    private async Task LogoutAsync()
    {
        var answer = MessageBox.Show(
            "현재 로그인 상태를 해제하고 로그인 화면으로 이동하시겠습니까?",
            "로그아웃",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            await FlushPendingChangesBeforeNavigationAsync("로그아웃", blockUntilServerFlush: true);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("AUTH", $"로그아웃 전 변경사항 저장 시도 실패: {ex.Message}");
        }

        if (Application.Current is App app)
            app.RequestRestartToLogin();

        Close();
    }

    private void RentalManagementButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null)
            return;

        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
    }

    private void RentalDashboardMenuItem_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenRentalDashboardWindowAsync, "렌탈 대시보드 창 열기");

    private void RentalBillingMenuItem_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenRentalBillingWindowAsync, "렌탈 청구관리 창 열기");

    private void RentalAssetMenuItem_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenRentalAssetWindowAsync, "렌탈 자산 창 열기");

    private void RentalSettingsMenuItem_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenRentalSettingsWindowAsync, "렌탈 설정 창 열기");

    // 전표 목록 탭의 수금 입력 버튼
    private void PaymentEntryButton_Click(object sender, RoutedEventArgs e)
        => RunUiAsync(OpenPaymentPopupAsync, "전표 목록 수금/지급 창 열기");

    private async Task OpenSalesWindowAsync(bool preselectSelectedCustomer)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        await OpenNewInvoiceWindowAsync(거래플랜.Shared.Contracts.VoucherType.Sales, preselectSelectedCustomer);
    }

    private async Task OpenPurchaseWindowAsync(bool preselectSelectedCustomer)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        await OpenNewInvoiceWindowAsync(거래플랜.Shared.Contracts.VoucherType.Purchase, preselectSelectedCustomer);
    }

    private async Task OpenProcurementWindowAsync(bool preselectSelectedCustomer)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        await OpenNewInvoiceWindowAsync(거래플랜.Shared.Contracts.VoucherType.Procurement, preselectSelectedCustomer);
    }

    private async Task OpenNewInvoiceWindowAsync(
        거래플랜.Shared.Contracts.VoucherType voucherType,
        bool preselectSelectedCustomer)
    {
        var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session, voucherType);
        await vm.LoadAsync();
        vm.NewInvoice();

        if (preselectSelectedCustomer &&
            _vm.SelectedCustomerFilter is not null &&
            vm.CanSelectCustomer(_vm.SelectedCustomerFilter))
        {
            vm.SetCustomer(_vm.SelectedCustomerFilter);
            vm.MarkCurrentStateAsPristine();
        }

        var win = new SalesWindow(vm) { Owner = this };
        win.Closed += SalesWindow_Closed;
        win.Show();
    }

    private async Task OpenInvoiceWindowAsync(Data.LocalInvoice invoice)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var entryType = invoice.VoucherType switch
        {
            거래플랜.Shared.Contracts.VoucherType.Purchase => 거래플랜.Shared.Contracts.VoucherType.Purchase,
            거래플랜.Shared.Contracts.VoucherType.Procurement => 거래플랜.Shared.Contracts.VoucherType.Procurement,
            _ => 거래플랜.Shared.Contracts.VoucherType.Sales
        };

        var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session, entryType);
        await vm.LoadAsync();
        await vm.LoadInvoiceAsync(invoice);

        var win = new SalesWindow(vm) { Owner = this };
        win.Closed += SalesWindow_Closed;
        win.Show();
    }

    private async Task OpenCustomerEditorAsync(Data.LocalCustomer? customer = null)
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new CustomerEditViewModel(_local, _session);
        await vm.LoadAsync(customer);

        var win = new CustomerEditWindow(vm) { Owner = this };
        if (win.ShowDialog() == true)
            await _vm.RefreshCustomersCommand.ExecuteAsync(null);
    }

    private async Task OpenInventoryWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new InventoryViewModel(_local, _session);
        await vm.LoadAsync();
        var win = new InventoryWindow(vm) { Owner = this };
        win.Show();
    }

    private async Task OpenPaymentPopupAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new PaymentViewModel(_local, _session);

        // 우선: 좌측 거래처 선택값, 없으면 선택 전표의 거래처를 사용
        var preselect = _vm.SelectedCustomerFilter;
        if (preselect is null && _vm.SelectedInvoiceRow is not null)
        {
            var invoice = await _local.GetInvoiceAsync(_vm.SelectedInvoiceRow.Id, _session);
            if (invoice is not null)
                preselect = await _local.GetCustomerAsync(invoice.CustomerId, _session);
        }

        await vm.LoadAsync(preselect);
        var win = new PaymentWindow(vm) { Owner = this };
        win.Show();
    }

    private async Task OpenYeonsuDeliveryWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new YeonsuDeliveryViewModel(_local, _session);
        await vm.InitializeAsync();
        var win = new YeonsuDeliveryWindow(vm, _local, _print, _invoicePrintService, _session)
        {
            Owner = this
        };
        win.Show();
    }

    private async Task OpenEnvironmentSettingsWindowAsync(EnvironmentSettingsInitialTab initialTab = EnvironmentSettingsInitialTab.General)
    {
        try
        {
            await FlushPendingChangesBeforeNavigationAsync("화면 전환");
            var vm = new EnvironmentSettingsViewModel(
                _local,
                _session,
                _api,
                _sync,
                _backup,
                _diagnostics,
                _rental,
                async () => await _vm.ReloadForBusinessDatabaseChangeAsync());
            await vm.InitializeAsync();
            var win = new EnvironmentSettingsWindow(vm, initialTab)
            {
                Owner = this
            };
            win.ShowDialog();

            if (!vm.BusinessDatabaseChanged)
                await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
        }
        catch (Exception ex)
        {
            AppLogger.Error("SETTINGS", "환경설정 창 열기 실패", ex);
            MessageBox.Show(
                $"환경설정을 여는 중 오류가 발생했습니다.{Environment.NewLine}{ex.Message}",
                "환경설정",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task OpenCustomerManagementWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new CustomerManagementViewModel(_local, _session);
        await vm.InitializeAsync();
        var win = new CustomerManagementWindow(vm, _local, _session)
        {
            Owner = this
        };
        win.ShowDialog();
        await _vm.RefreshCustomersCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalCustomerOnboardingAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var onboardingViewModel = new RentalCustomerOnboardingViewModel(_rental, _local, _session);
        await onboardingViewModel.LoadAsync();

        var onboardingWindow = new RentalCustomerOnboardingWindow(onboardingViewModel)
        {
            Owner = this
        };

        onboardingWindow.ShowDialog();
        if (!onboardingViewModel.IsCompleted)
            return;

        await _vm.RefreshCustomersCommand.ExecuteAsync(null);
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalDashboardWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new RentalDashboardViewModel(_rental, _session);
        await vm.LoadAsync();
        var win = new RentalDashboardWindow(vm)
        {
            Owner = this
        };
        win.ShowDialog();
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalBillingWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new RentalBillingViewModel(_rental, _local, _session);
        await vm.LoadAsync();
        var win = new RentalBillingWindow(vm)
        {
            Owner = this
        };
        win.ShowDialog();

        if (vm.InvoiceToOpenAfterClose.HasValue)
        {
            var invoice = await _local.GetInvoiceAsync(vm.InvoiceToOpenAfterClose.Value);
            if (invoice is not null)
                await OpenInvoiceWindowAsync(invoice);
        }

        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalAssetWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new RentalAssetViewModel(_rental, _local, _rentalDocuments, _invoicePrintService, _session);
        await vm.LoadAsync();
        var win = new RentalAssetWindow(vm)
        {
            Owner = this
        };
        win.ShowDialog();
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private async Task OpenRentalSettingsWindowAsync()
    {
        await FlushPendingChangesBeforeNavigationAsync("화면 전환");
        var vm = new RentalSettingsViewModel(_rental, _local, _session);
        await vm.LoadAsync();
        var win = new RentalSettingsWindow(vm)
        {
            Owner = this
        };
        win.ShowDialog();
        await _vm.LoadInvoiceListCommand.ExecuteAsync(null);
    }

    private void CentralRevisionPollTimer_Tick(object? sender, EventArgs e)
        => UiTaskHelper.Forget(
            PollCentralRevisionAsync(),
            "UI",
            "중앙 revision polling",
            ex => AppLogger.Warn("SYNC", $"중앙 revision polling 실패: {ex.Message}"));

    private void SalesWindow_Closed(object? sender, EventArgs e)
        => RunUiAsync(
            () => _vm.LoadInvoiceListCommand.ExecuteAsync(null),
            "전표 창 종료 후 목록 재조회",
            "전표 목록을 다시 불러오는 중 오류가 발생했습니다.");

    private async Task FlushPendingChangesBeforeNavigationAsync(string reason, bool blockUntilServerFlush = false)
    {
        if (_isClosingOrClosed || _session.IsOfflineMode)
            return;

        if (!blockUntilServerFlush && _sync.HasActiveOrQueuedSync)
            return;

        var dirtyCount = await _local.CountDirtyAsync(_session);
        if (dirtyCount == 0)
            return;

        try
        {
            if (!blockUntilServerFlush)
            {
                _vm.SyncStatus = $"{reason} 전 변경사항을 백그라운드로 동기화합니다...";
                UiTaskHelper.Forget(
                    _sync.TrySyncAsync(),
                    "SYNC",
                    $"{reason} 백그라운드 동기화",
                    ex => AppLogger.Warn("SYNC", $"{reason} background sync failed: {ex.Message}"));
                return;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _vm.SyncStatus = $"{reason} 전 중앙 서버에 변경사항 저장 중...";
            await _sync.FlushPendingChangesAsync(cts.Token);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("SYNC", $"{reason} flush failed: {ex.Message}");
        }
    }

    private async Task CheckAndPromptForDesktopUpdateAsync()
    {
        if (_isClosingOrClosed || _updatePromptInProgress || _session.IsOfflineMode)
            return;

        _updatePromptInProgress = true;
        try
        {
            var result = await _updateService.CheckForUpdatesAsync();
            if (!result.IsUpdateAvailable || result.Package is null)
                return;

            var lastPromptedVersion = await _local.GetSettingAsync("Update.LastPromptedDesktopVersion");
            if (string.Equals(lastPromptedVersion, result.LatestVersion, StringComparison.OrdinalIgnoreCase))
                return;

            await _local.SetSettingAsync("Update.LastPromptedDesktopVersion", result.LatestVersion, CancellationToken.None);

            var answer = MessageBox.Show(
                $"새 PC 버전 {result.LatestVersion}이 준비되어 있습니다.{Environment.NewLine}{Environment.NewLine}" +
                "지금 업데이트를 시작하시겠습니까?",
                "업데이트 알림",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer != MessageBoxResult.Yes)
                return;

            _updateService.StartUpdate(result.Package);
            _vm.SyncStatus = $"업데이트 {result.LatestVersion} 설치를 시작했습니다.";
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Application.Current.Shutdown()));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UPDATE", $"Desktop update prompt failed: {ex.Message}");
        }
        finally
        {
            _updatePromptInProgress = false;
        }
    }
}
