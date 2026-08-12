using System.Text.RegularExpressions;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class NativeCommonDialogSourceGuardTests
{
    [Fact]
    public void DesktopNativeFileDialogs_UseTrackedShutdownAwareHelper()
    {
        var appRoot = FindDesktopAppRoot();
        var declarationPattern = new Regex(
            @"var\s+(?<name>\w+)\s*=\s*new\s+(?:OpenFileDialog|SaveFileDialog)",
            RegexOptions.CultureInvariant);
        var nativeDialogCount = 0;
        var wrappedDialogCount = 0;

        foreach (var sourceFile in EnumerateDesktopSources(appRoot))
        {
            var source = File.ReadAllText(sourceFile);
            var declarations = declarationPattern.Matches(source);
            nativeDialogCount += declarations.Count;
            foreach (Match declaration in declarations)
            {
                var variableName = declaration.Groups["name"].Value;
                Assert.DoesNotMatch(
                    $@"\b{Regex.Escape(variableName)}\.ShowDialog\s*\(",
                    source);
            }

            if (declarations.Count > 0)
            {
                wrappedDialogCount += Regex.Matches(
                    source,
                    @"DialogWindowCloseHelper\.ShowDialog\s*\(\s*(?:dialog|dlg)\b",
                    RegexOptions.CultureInvariant).Count;
            }
        }

        Assert.Equal(18, nativeDialogCount);
        Assert.Equal(18, wrappedDialogCount);
    }

    [Fact]
    public void NativeDialogHelper_RejectsShutdownByDefaultAndTracksUntilFinally()
    {
        var helper = File.ReadAllText(Path.Combine(
            FindDesktopAppRoot(),
            "Infrastructure",
            "DialogWindowCloseHelper.cs"));

        Assert.Contains("CommonDialog dialog", helper, StringComparison.Ordinal);
        Assert.Contains("Window? owner = null", helper, StringComparison.Ordinal);
        Assert.Contains("bool allowDuringShutdown = false", helper, StringComparison.Ordinal);
        Assert.Contains("if (!allowDuringShutdown &&", helper, StringComparison.Ordinal);
        Assert.Contains("mainWindow.IsShutdownProtectionActive", helper, StringComparison.Ordinal);
        Assert.Contains("ActiveNativeDialogs.Add(dialog)", helper, StringComparison.Ordinal);
        Assert.Contains("finally", helper, StringComparison.Ordinal);
        Assert.Contains("ActiveNativeDialogs.Remove(dialog)", helper, StringComparison.Ordinal);
        Assert.Contains("public static int ActiveNativeDialogCount", helper, StringComparison.Ordinal);
        Assert.Contains("public static Task WaitForNoActiveNativeDialogsAsync()", helper, StringComparison.Ordinal);
        Assert.Contains("ActiveNativeDialogs.Count == 0", helper, StringComparison.Ordinal);
        Assert.Contains("NativeDialogsDrained.Task", helper, StringComparison.Ordinal);
        Assert.Contains("drained?.TrySetResult();", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("FindWindow", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("SendMessage", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowShutdown_AbortsBoundedCloseOrWaitsForForcedDrain()
    {
        var mainWindow = File.ReadAllText(Path.Combine(
            FindDesktopAppRoot(),
            "MainWindow.xaml.cs"));

        Assert.Contains(
            "DialogWindowCloseHelper.ActiveNativeDialogCount > 0",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (!waitForCompletionWithoutDeadline)",
            mainWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "await DialogWindowCloseHelper.WaitForNoActiveNativeDialogsAsync();",
            mainWindow,
            StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateDesktopSources(string appRoot)
        => Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

    private static string FindDesktopAppRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var desktopRoot = Path.Combine(directory.FullName, "Desktop");
            if (Directory.Exists(desktopRoot) &&
                Directory.EnumerateFiles(directory.FullName, "*.sln", SearchOption.TopDirectoryOnly).Any())
            {
                return Directory.EnumerateDirectories(desktopRoot, "*.Desktop.App", SearchOption.TopDirectoryOnly)
                    .Single();
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Desktop app root was not found.");
    }
}
