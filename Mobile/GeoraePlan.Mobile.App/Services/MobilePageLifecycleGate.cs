namespace GeoraePlan.Mobile.App.Services;

internal sealed class MobilePageLifecycleGate
{
    private long _epoch;
    private bool _isVisible;

    public long Enter()
    {
        _isVisible = true;
        return ++_epoch;
    }

    public void Exit()
    {
        _isVisible = false;
        _epoch++;
    }

    public long Capture() =>
        _epoch;

    public bool TryCommit(long epoch, Action action)
    {
        if (!_isVisible || epoch != _epoch)
            return false;

        action();
        return true;
    }

    public bool TryCommitTopPage<TPage>(
        long epoch,
        IReadOnlyList<TPage> navigationStack,
        TPage currentPage,
        Action action)
        where TPage : class
    {
        ArgumentNullException.ThrowIfNull(
            navigationStack);
        ArgumentNullException.ThrowIfNull(
            currentPage);
        ArgumentNullException.ThrowIfNull(action);

        if (!_isVisible ||
            epoch != _epoch ||
            navigationStack.Count <= 1 ||
            !ReferenceEquals(
                navigationStack[^1],
                currentPage))
        {
            return false;
        }

        action();
        return true;
    }
}
