using System.IO;

namespace 거래플랜.Desktop.App.Infrastructure;

public static class AppRuntimeInfo
{
    private const string TestModeEnvironmentKey = "GEORAEPLAN_TEST_MODE";
    private static readonly Lazy<bool> IsTestRuntimeCache = new(DetermineIsTestRuntime);

    public static bool IsTestRuntime => IsTestRuntimeCache.Value;

    public static string WithTestLabel(string? title)
    {
        var trimmedTitle = (title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(trimmedTitle))
            return trimmedTitle;

        if (!IsTestRuntime)
            return trimmedTitle;

        return trimmedTitle.Contains("(테스트)", StringComparison.Ordinal)
            ? trimmedTitle
            : $"{trimmedTitle} (테스트)";
    }

    private static bool DetermineIsTestRuntime()
    {
        if (IsTruthy(Environment.GetEnvironmentVariable(TestModeEnvironmentKey)))
            return true;

        if (LooksLikeTestRuntimePath(Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")))
            return true;

        return LooksLikeTestRuntimePath(AppContext.BaseDirectory);
    }

    private static bool LooksLikeTestRuntimePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string normalizedPath;
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch
        {
            normalizedPath = path;
        }

        return normalizedPath.IndexOf("테스트 시행", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsTruthy(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        return string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(raw, "on", StringComparison.OrdinalIgnoreCase);
    }
}
