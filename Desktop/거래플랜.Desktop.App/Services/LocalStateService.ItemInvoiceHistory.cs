namespace 거래플랜.Desktop.App.Services;

public sealed class DesktopDataChangeNotifier
{
    public event EventHandler? InventoryStateChanged;
    public event EventHandler? ItemInvoiceHistoryChanged;

    internal bool TryPublishInventoryStateChanged(
        object sender,
        Func<bool>? canPublish = null,
        Func<IDisposable>? enterCallbackScope = null)
        => TryPublish(
            InventoryStateChanged,
            sender,
            canPublish,
            enterCallbackScope);

    internal bool TryPublishItemInvoiceHistoryChanged(
        object sender,
        Func<bool>? canPublish = null,
        Func<IDisposable>? enterCallbackScope = null)
        => TryPublish(
            ItemInvoiceHistoryChanged,
            sender,
            canPublish,
            enterCallbackScope);

    private static bool TryPublish(
        EventHandler? handlers,
        object sender,
        Func<bool>? canPublish,
        Func<IDisposable>? enterCallbackScope)
    {
        if (canPublish is not null && !canPublish())
            return false;
        if (handlers is null)
            return canPublish?.Invoke() ?? true;

        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            if (canPublish is not null && !canPublish())
                return false;

            try
            {
                using var callbackScope =
                    enterCallbackScope?.Invoke();
                handler(sender, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "LOCAL",
                    "데스크톱 데이터 변경 알림 구독자 처리 중 오류가 발생했습니다.",
                    ex);
            }
        }

        return canPublish?.Invoke() ?? true;
    }
}

public sealed partial class LocalStateService
{
    private readonly DesktopDataChangeNotifier _dataChangeNotifier;
    private InventoryStateChangeCapture? _activeInventoryStateChangeCapture;

    public event EventHandler? InventoryStateChanged
    {
        add => _dataChangeNotifier.InventoryStateChanged += value;
        remove => _dataChangeNotifier.InventoryStateChanged -= value;
    }

    public event EventHandler? ItemInvoiceHistoryChanged
    {
        add => _dataChangeNotifier.ItemInvoiceHistoryChanged += value;
        remove => _dataChangeNotifier.ItemInvoiceHistoryChanged -= value;
    }

    internal InventoryStateChangeCapture CaptureInventoryStateChanges()
        => new(this);

    internal void RecordInventoryStateChanged()
        => RaiseInventoryStateChanged();

    internal bool TryPublishInventoryStateChanged(
        Func<bool>? canPublish = null,
        Func<IDisposable>? enterCallbackScope = null)
        => _dataChangeNotifier.TryPublishInventoryStateChanged(
            this,
            canPublish,
            enterCallbackScope);

    internal bool TryPublishItemInvoiceHistoryChanged(
        Func<bool>? canPublish = null,
        Func<IDisposable>? enterCallbackScope = null)
        => _dataChangeNotifier.TryPublishItemInvoiceHistoryChanged(
            this,
            canPublish,
            enterCallbackScope);

    internal sealed class InventoryStateChangeCapture : IDisposable
    {
        private LocalStateService? _owner;
        private readonly InventoryStateChangeCapture? _parent;

        internal InventoryStateChangeCapture(LocalStateService owner)
        {
            _owner = owner;
            _parent = owner._activeInventoryStateChangeCapture;
            owner._activeInventoryStateChangeCapture = this;
        }

        public bool HasChanges { get; private set; }

        internal void RecordChange()
        {
            HasChanges = true;
            _parent?.RecordChange();
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null &&
                ReferenceEquals(owner._activeInventoryStateChangeCapture, this))
            {
                owner._activeInventoryStateChangeCapture = _parent;
            }
        }
    }
}
