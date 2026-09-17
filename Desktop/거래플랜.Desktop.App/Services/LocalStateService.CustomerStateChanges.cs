namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private CustomerStateChangeCapture? _activeCustomerStateChangeCapture;

    public event EventHandler? CustomerStateChanged
    {
        add => _dataChangeNotifier.CustomerStateChanged += value;
        remove => _dataChangeNotifier.CustomerStateChanged -= value;
    }

    internal CustomerStateChangeCapture CaptureCustomerStateChanges() => new(this);
    internal void RecordCustomerStateChanged() => _activeCustomerStateChangeCapture?.RecordChange();
    internal bool TryPublishCustomerStateChanged(Func<bool>? canPublish = null,
        Func<IDisposable>? enterCallbackScope = null)
        => _dataChangeNotifier.TryPublishCustomerStateChanged(this, canPublish, enterCallbackScope);

    // Capturing never publishes: only the successful transaction's owner may notify the UI.
    internal sealed class CustomerStateChangeCapture : IDisposable
    {
        private LocalStateService? _owner;
        private readonly CustomerStateChangeCapture? _parent;
        internal CustomerStateChangeCapture(LocalStateService owner)
        {
            _owner = owner;
            _parent = owner._activeCustomerStateChangeCapture;
            owner._activeCustomerStateChangeCapture = this;
        }
        public bool HasChanges { get; private set; }
        internal void RecordChange() { HasChanges = true; }
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null && ReferenceEquals(owner._activeCustomerStateChangeCapture, this))
                owner._activeCustomerStateChangeCapture = _parent;
        }
    }
}
