namespace 거래플랜.Desktop.App.Services;

public sealed partial class DataIntegrityItemDuplicateCandidate
{
    internal bool PurchaseAmountsHidden { get; init; }
    internal bool SalesAmountsHidden { get; init; }
    private SessionState? _amountSession;
    private FinancialAmountVisibility.AccessKey _amountAccess;

    // Raw values are used only by conflict detection and the stable snapshot hash.
    // Public getters are closed until the result is bound to its originating read.
    internal void ProtectAmounts(SessionState session, FinancialAmountVisibility.AccessKey access)
    {
        if (_amountSession is not null) return;
        _amountSession = session;
        _amountAccess = access;
    }

    private bool CanShowAmount(bool purchase) => _amountSession?.IsLoggedIn == true
        && FinancialAmountVisibility.CaptureAccess(_amountSession) == _amountAccess
        && !(purchase ? PurchaseAmountsHidden : SalesAmountsHidden)
        && (purchase ? _amountAccess.Purchase : _amountAccess.Sales);

    private static string DisplayAmount(decimal? value) => value?.ToString("N0") ?? "비공개";

    internal void RefreshAmountAccess()
    {
        OnPropertyChanged(nameof(PurchasePrice));
        OnPropertyChanged(nameof(SalePrice));
        OnPropertyChanged(nameof(RetailPrice));
        OnPropertyChanged(nameof(PriceGradeA));
        OnPropertyChanged(nameof(PriceGradeB));
        OnPropertyChanged(nameof(PriceGradeC));
        OnPropertyChanged(nameof(MasterDataSummary));
    }
}

public sealed partial class DataIntegrityItemDuplicateComparison
{
    private SessionState? _amountSession;
    private bool _amountAccessAttached;

    internal void ProtectAmounts(SessionState session, FinancialAmountVisibility.AccessKey access)
    {
        // A fallback comparison must never be rebound to a later login or scope.
        if (_amountSession is not null) return;
        _amountSession = session;
        foreach (var candidate in Candidates) candidate.ProtectAmounts(session, access);
    }

    internal void AttachAmountAccess()
    {
        if (_amountAccessAttached || _amountSession is null) return;
        _amountSession.AccessChanged += OnAmountAccessChanged;
        _amountAccessAttached = true;
        OnAmountAccessChanged(this, EventArgs.Empty);
    }

    internal void DetachAmountAccess()
    {
        if (!_amountAccessAttached || _amountSession is null) return;
        _amountSession.AccessChanged -= OnAmountAccessChanged;
        _amountAccessAttached = false;
    }

    private void OnAmountAccessChanged(object? sender, EventArgs e)
    {
        void Refresh()
        {
            foreach (var candidate in Candidates) candidate.RefreshAmountAccess();
        }
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.InvokeAsync(Refresh);
        else Refresh();
    }
}
