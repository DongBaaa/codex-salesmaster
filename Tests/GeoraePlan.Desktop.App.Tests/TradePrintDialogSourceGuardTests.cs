using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class TradePrintDialogSourceGuardTests
{
    [Fact]
    public void TradePrintWindow_ProvidesXpsFileSaveFallback()
    {
        var repoRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml"));
        var executor = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "TradePrintExecutor.cs"));

        Assert.Contains("파일 저장(XPS)", xaml, StringComparison.Ordinal);
        Assert.Contains("SaveDocumentAsXps", executor, StringComparison.Ordinal);
        Assert.Contains("XpsDocument.CreateXpsDocumentWriter", executor, StringComparison.Ordinal);
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
