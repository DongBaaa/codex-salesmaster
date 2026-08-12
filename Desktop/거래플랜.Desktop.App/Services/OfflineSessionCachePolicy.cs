using System.Globalization;

namespace 거래플랜.Desktop.App.Services;

internal static class OfflineSessionCachePolicy
{
    internal const string MaximumOfflineGraceHoursEnvironmentKey = "GEORAEPLAN_OFFLINE_GRACE_HOURS";

    internal const int CurrentSchemaVersion = 4;

    internal static readonly TimeSpan DefaultMaximumOfflineGrace = TimeSpan.FromHours(24);

    internal static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan MaximumClockRollback = TimeSpan.Zero;

    internal static TimeSpan ResolveMaximumOfflineGrace()
        => ResolveMaximumOfflineGrace(
            Environment.GetEnvironmentVariable(MaximumOfflineGraceHoursEnvironmentKey));

    internal static TimeSpan ResolveMaximumOfflineGrace(string? configuredHours)
    {
        if (!double.TryParse(
                configuredHours,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var hours)
            || !double.IsFinite(hours)
            || hours < 0)
        {
            return DefaultMaximumOfflineGrace;
        }

        if (hours >= DefaultMaximumOfflineGrace.TotalHours)
            return DefaultMaximumOfflineGrace;

        return NormalizeMaximumOfflineGrace(TimeSpan.FromHours(hours));
    }

    internal static TimeSpan NormalizeMaximumOfflineGrace(TimeSpan configuredGrace)
    {
        if (configuredGrace < TimeSpan.Zero)
            return DefaultMaximumOfflineGrace;

        return configuredGrace <= DefaultMaximumOfflineGrace
            ? configuredGrace
            : DefaultMaximumOfflineGrace;
    }

    internal static bool IsFresh(
        DateTimeOffset cachedAtUtc,
        DateTimeOffset lastOnlineValidationAtUtc,
        DateTimeOffset lastAcceptedOfflineUtc,
        DateTimeOffset nowUtc,
        TimeSpan maximumOfflineGrace)
    {
        cachedAtUtc = cachedAtUtc.ToUniversalTime();
        lastOnlineValidationAtUtc = lastOnlineValidationAtUtc.ToUniversalTime();
        lastAcceptedOfflineUtc = lastAcceptedOfflineUtc.ToUniversalTime();
        nowUtc = nowUtc.ToUniversalTime();
        maximumOfflineGrace = NormalizeMaximumOfflineGrace(maximumOfflineGrace);

        if (cachedAtUtc > lastOnlineValidationAtUtc
            || lastAcceptedOfflineUtc < lastOnlineValidationAtUtc)
            return false;

        if (cachedAtUtc - nowUtc > MaximumFutureClockSkew
            || lastOnlineValidationAtUtc - nowUtc > MaximumFutureClockSkew
            || lastAcceptedOfflineUtc - nowUtc > MaximumClockRollback)
        {
            return false;
        }

        var age = nowUtc - lastOnlineValidationAtUtc;
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        return age <= maximumOfflineGrace;
    }
}
