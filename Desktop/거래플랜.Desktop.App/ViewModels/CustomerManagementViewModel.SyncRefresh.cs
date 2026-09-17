using System.Windows.Threading;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class CustomerManagementViewModel
{
    private readonly Dispatcher _customerDispatcher = Dispatcher.CurrentDispatcher;
    private Guid _customerSessionId;
    private long _customerScopeEpoch;
    private bool _customerRefreshPending;
    private bool _customerRefreshRunning;
    public bool IsRefreshingRows { get; private set; }

    private bool IsCustomerOwnerCurrent =>
        _session.SessionId == _customerSessionId && _session.SyncScopeEpoch == _customerScopeEpoch;

    private void SubscribeCustomerChanges()
    {
        _customerSessionId = _session.SessionId;
        _customerScopeEpoch = _session.SyncScopeEpoch;
        _local.CustomerStateChanged += OnCustomerStateChanged;
        _session.BusinessDatabaseChanged += OnCustomerStateChanged;
    }

    private void OnCustomerStateChanged(object? sender, EventArgs args)
    {
        if (_isDisposed || _customerDispatcher.HasShutdownStarted)
            return;
        // Never query from a post-commit callback: it may still hold the sync owner lease.
        _customerDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(QueueCustomerRefresh));
    }

    private async void QueueCustomerRefresh()
    {
        if (_isDisposed)
            return;
        _customerRefreshPending = true;
        if (_customerRefreshRunning)
            return;
        _customerRefreshRunning = true;
        try
        {
            while (_customerRefreshPending && !_isDisposed)
            {
                _customerRefreshPending = false;
                if (!IsCustomerOwnerCurrent)
                {
                    InvalidateCustomerList();
                    return;
                }
                await ReloadAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("UI", "동기화 후 거래처 목록 갱신 실패", ex);
            if (!_isDisposed)
                StatusMessage = "거래처 목록을 갱신하지 못했습니다. 새로고침을 눌러 다시 확인하세요.";
        }
        finally { _customerRefreshRunning = false; }
    }

    private void InvalidateCustomerList()
    {
        DetachRowHandlers();
        _allRows.Clear();
        Customers.Clear();
        _contractSummaryMap.Clear();
        _categoryNames.Clear();
        RefreshContractAlertState([]);
        SelectedCustomer = null;
        IsBusy = false;
        StatusMessage = "로그인 또는 업무 범위가 변경되었습니다. 거래처 관리 창을 다시 열어 주세요.";
        Dispose();
    }
}
