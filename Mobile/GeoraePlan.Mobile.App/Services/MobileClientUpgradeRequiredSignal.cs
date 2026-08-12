namespace GeoraePlan.Mobile.App.Services;

internal static class MobileClientUpgradeRequiredSignal
{
    public static event Action<MobileClientUpgradeRequiredException>? Raised;

    public static void Publish(MobileClientUpgradeRequiredException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var subscribers = Raised;
        if (subscribers is null)
            return;

        foreach (Action<MobileClientUpgradeRequiredException> subscriber
                 in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(exception);
            }
            catch (Exception ex)
            {
                // A UI observer must never replace the typed 426 exception that
                // the API caller relies on for pending-data preservation.
                MobileAppLogger.Warn(
                    "UPDATE",
                    $"강제 업데이트 신호 구독자 처리 실패: {ex.Message}");
            }
        }
    }
}
