using System.Runtime.CompilerServices;
using \uac70\ub798\ud50c\ub79c.Desktop.App.Infrastructure;

namespace GeoraePlan.Desktop.App.Tests;

internal static class TestProcessIsolation
{
    internal static string AppRoot { get; private set; } = string.Empty;
    internal static string DownloadsRoot { get; private set; } = string.Empty;
    internal static string OriginalUserAppRoot { get; private set; } = string.Empty;
    internal static string TempRoot { get; private set; } = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        OriginalUserAppRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "\uac70\ub798\ud50c\ub79c"));

        var configuredAppRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        AppRoot = string.IsNullOrWhiteSpace(configuredAppRoot)
            ? CreateDefaultAppRoot()
            : Path.GetFullPath(configuredAppRoot);

        var processStateRoot = Path.Combine(AppRoot, ".test-process");
        TempRoot = ResolveOrDefault("GEORAEPLAN_TEMP_ROOT", Path.Combine(processStateRoot, "temp"));
        DownloadsRoot = ResolveOrDefault("GEORAEPLAN_DOWNLOADS_ROOT", Path.Combine(processStateRoot, "downloads"));
        var localAppDataRoot = Path.Combine(processStateRoot, "local-app-data");
        var roamingAppDataRoot = Path.Combine(processStateRoot, "roaming-app-data");
        var expectedDrive = Path.GetPathRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("The desktop test output drive could not be determined.");
        var profileAppRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData",
            "Local",
            "\uac70\ub798\ud50c\ub79c");

        foreach (var path in new[]
                 {
                     AppRoot,
                     processStateRoot,
                     TempRoot,
                     DownloadsRoot,
                     localAppDataRoot,
                     roamingAppDataRoot
                 })
        {
            EnsureSafeSandboxPath(path, expectedDrive, OriginalUserAppRoot, profileAppRoot);
            Directory.CreateDirectory(path);
            EnsureSafeSandboxPath(path, expectedDrive, OriginalUserAppRoot, profileAppRoot);
        }

        Environment.SetEnvironmentVariable("GEORAEPLAN_TEST_MODE", "1");
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", AppRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_TEMP_ROOT", TempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DOWNLOADS_ROOT", DownloadsRoot);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppDataRoot);
        Environment.SetEnvironmentVariable("APPDATA", roamingAppDataRoot);
        Environment.SetEnvironmentVariable("TEMP", TempRoot);
        Environment.SetEnvironmentVariable("TMP", TempRoot);
        Environment.SetEnvironmentVariable(
            "PSModulePath",
            GetWindowsPowerShellModulePath());

        _ = AppPaths.LocalDbFile;

        if (!IsWithin(AppPaths.LocalDbFile, AppRoot))
        {
            throw new InvalidOperationException(
                $"Desktop test database escaped the isolated root: {AppPaths.LocalDbFile}");
        }
    }

    private static string GetWindowsPowerShellModulePath()
    {
        var windowsPowerShellHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0");
        var modulePaths = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "WindowsPowerShell",
                "Modules"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "WindowsPowerShell",
                "Modules"),
            Path.Combine(windowsPowerShellHome, "Modules")
        };

        return string.Join(
            Path.PathSeparator,
            modulePaths.Where(Directory.Exists).Distinct(
                StringComparer.OrdinalIgnoreCase));
    }

    internal static bool IsWithin(string candidatePath, string parentPath)
    {
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentPath));

        return string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                parent + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateDefaultAppRoot()
    {
        var storageRoot = Directory.Exists(@"D:\")
            ? @"D:\DevCaches\georaeplan-v1-tests"
            : Path.Combine(Path.GetTempPath(), "georaeplan-v1-tests");

        return Path.Combine(
            storageRoot,
            $"{Environment.ProcessId}-{Guid.NewGuid():N}",
            "app-root");
    }

    private static string ResolveOrDefault(string environmentKey, string defaultPath)
    {
        var configuredPath = Environment.GetEnvironmentVariable(environmentKey);
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.GetFullPath(defaultPath)
            : Path.GetFullPath(configuredPath);
    }

    private static void EnsureSafeSandboxPath(
        string path,
        string expectedDrive,
        params string[] protectedRoots)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(
                Path.GetPathRoot(fullPath),
                expectedDrive,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Desktop tests require all writable paths on {expectedDrive}: {fullPath}");
        }

        if (protectedRoots.Any(root => !string.IsNullOrWhiteSpace(root) && PathsOverlap(fullPath, root)))
        {
            throw new InvalidOperationException(
                $"Desktop tests refused a path that overlaps real user application data: {fullPath}");
        }

        if (!HasNoExistingReparsePointInPathChain(fullPath))
        {
            throw new InvalidOperationException(
                $"Desktop tests refused a sandbox path containing a symbolic link or junction: {fullPath}");
        }
    }

    internal static bool HasNoExistingReparsePointInPathChain(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return false;

            var relativePath = fullPath[root.Length..];
            var current = root;
            foreach (var segment in relativePath.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current) && !File.Exists(current))
                    break;

                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsOverlap(string left, string right)
    {
        var normalizedLeft = Path.TrimEndingDirectorySeparator(Path.GetFullPath(left));
        var normalizedRight = Path.TrimEndingDirectorySeparator(Path.GetFullPath(right));
        if (string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase))
            return true;

        return IsWithin(normalizedLeft, normalizedRight)
            || IsWithin(normalizedRight, normalizedLeft);
    }
}
