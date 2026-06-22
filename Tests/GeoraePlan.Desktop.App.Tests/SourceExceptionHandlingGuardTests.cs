using System.Text.RegularExpressions;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SourceExceptionHandlingGuardTests
{
    [Fact]
    public void ApplicationSources_DoNotContainEmptyCatchBlocks()
    {
        var repoRoot = FindRepositoryRoot();
        var sourceRoots = new[]
        {
            Path.Combine(repoRoot, "Desktop"),
            Path.Combine(repoRoot, "Mobile"),
            Path.Combine(repoRoot, "Server"),
            Path.Combine(repoRoot, "Shared")
        };

        var emptyCatchPattern = new Regex(
            @"catch\s*(\([^)]*\))?\s*\{\s*\}",
            RegexOptions.Compiled | RegexOptions.Multiline);

        var offenders = sourceRoots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            .Select(path => new
            {
                Path = Path.GetRelativePath(repoRoot, path),
                Matches = emptyCatchPattern.Matches(File.ReadAllText(path)).Select(match => match.Index).ToArray()
            })
            .Where(result => result.Matches.Length > 0)
            .Select(result => result.Path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Empty catch blocks hide failed save/sync/schema/printing work. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void DesktopBestEffortFailurePaths_AreLogged()
    {
        var repoRoot = FindRepositoryRoot();

        var localDbInitializer = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Data",
            "LocalDbInitializer.cs"));
        Assert.Contains("LogSchemaSqlFailure", localDbInitializer, StringComparison.Ordinal);
        Assert.Contains("LogSchemaStepFailure", localDbInitializer, StringComparison.Ordinal);

        var rentalBillingAutoSave = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "RentalBillingViewModel.AutoSave.cs"));
        Assert.Contains("RENTAL-AUTOSAVE", rentalBillingAutoSave, StringComparison.Ordinal);

        var printService = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "WpfInvoicePrintService.cs"));
        Assert.Contains("AppLogger.Warn(\"PRINT\"", printService, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "거래플랜.sln")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
