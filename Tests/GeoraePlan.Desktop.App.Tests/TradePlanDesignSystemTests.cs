using System.Text.RegularExpressions;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class TradePlanDesignSystemTests
{
    [Fact]
    public void App_MergesSharedTradePlanDesignSystem()
    {
        var root = FindRepositoryRoot();
        var appXaml = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "App.xaml"));
        var designSystem = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Themes",
            "TradePlanDesignSystem.xaml"));

        Assert.Contains("Source=\"Themes/TradePlanDesignSystem.xaml\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TradePlanPrimaryButtonStyle\"", designSystem, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TradePlanEditButtonStyle\"", designSystem, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TradePlanSecondaryButtonStyle\"", designSystem, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TradePlanDangerButtonStyle\"", designSystem, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TradePlanPrintButtonStyle\"", designSystem, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TradePlanCompactButtonStyle\"", designSystem, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TradePlanHeaderPanelStyle\"", designSystem, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TradePlanWindowTitleStyle\"", designSystem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MainWindow.xaml", "TradePlanEditButtonStyle", "TradePlanDangerButtonStyle")]
    [InlineData("Views/SalesWindow.xaml", "TradePlanPrimaryButtonStyle", "TradePlanSecondaryButtonStyle")]
    [InlineData("Views/RentalBillingWindow.xaml", "TradePlanHeaderPanelStyle", "TradePlanCompactButtonStyle")]
    [InlineData("Views/RentalAssetWindow.xaml", "TradePlanHeaderPanelStyle", "TradePlanPrimaryButtonStyle")]
    [InlineData("Views/EnvironmentSettingsWindow.xaml", "TradePlanPrimaryButtonStyle", "TradePlanDangerButtonStyle")]
    public void RepresentativeWindows_UseSharedActionHierarchy(
        string relativePath,
        string firstStyle,
        string secondStyle)
    {
        var root = FindRepositoryRoot();
        var pathSegments = relativePath.Split('/');
        var xamlPath = Path.Combine(
            new[] { root, "Desktop", "거래플랜.Desktop.App" }
                .Concat(pathSegments)
                .ToArray());
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains($"StaticResource {firstStyle}", xaml, StringComparison.Ordinal);
        Assert.Contains($"StaticResource {secondStyle}", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryWindowButtonBaseline_InheritsSharedSecondaryStyle()
    {
        var root = FindRepositoryRoot();
        var appRoot = Path.Combine(root, "Desktop", "거래플랜.Desktop.App");
        var appXaml = File.ReadAllText(Path.Combine(appRoot, "App.xaml"));

        Assert.Contains(
            "<Style TargetType=\"Button\" BasedOn=\"{StaticResource TradePlanSecondaryButtonStyle}\">",
            appXaml,
            StringComparison.Ordinal);

        var failures = new List<string>();
        foreach (var xamlPath in EnumerateWindowXamlFiles(appRoot))
        {
            var xaml = File.ReadAllText(xamlPath);
            foreach (Match match in Regex.Matches(
                         xaml,
                         "<Style\\s+TargetType=\"Button\"[^>]*>",
                         RegexOptions.CultureInvariant))
            {
                if (!match.Value.Contains(
                        "BasedOn=\"{StaticResource TradePlanSecondaryButtonStyle}\"",
                        StringComparison.Ordinal))
                {
                    failures.Add($"{Path.GetFileName(xamlPath)}:{GetLineNumber(xaml, match.Index)}");
                }
            }
        }

        Assert.True(failures.Count == 0, $"공통 버튼 기반 스타일이 누락됐습니다: {string.Join(", ", failures)}");
    }

    [Fact]
    public void SemanticActionButtons_KeepSaveDeleteAndPrintRolesConsistent()
    {
        var root = FindRepositoryRoot();
        var appRoot = Path.Combine(root, "Desktop", "거래플랜.Desktop.App");
        var failures = new List<string>();

        foreach (var xamlPath in EnumerateWindowXamlFiles(appRoot))
        {
            var xaml = File.ReadAllText(xamlPath);
            foreach (Match match in Regex.Matches(
                         xaml,
                         "<Button\\b[^>]*Content=\"(?<content>[^\"]+)\"[^>]*>",
                         RegexOptions.CultureInvariant | RegexOptions.Singleline))
            {
                var content = match.Groups["content"].Value;
                var tag = match.Value;
                var location = $"{Path.GetFileName(xamlPath)}:{GetLineNumber(xaml, match.Index)} {content}";

                if ((content.Contains("삭제", StringComparison.Ordinal) ||
                     content.Contains("반려", StringComparison.Ordinal)) &&
                    !tag.Contains("Danger", StringComparison.Ordinal) &&
                    !tag.Contains("Destructive", StringComparison.Ordinal))
                {
                    failures.Add($"삭제 역할: {location}");
                }

                if (content.Contains("저장", StringComparison.Ordinal) &&
                    !content.Contains("PDF", StringComparison.Ordinal) &&
                    !content.Contains("XPS", StringComparison.Ordinal) &&
                    !content.Contains("엑셀", StringComparison.Ordinal) &&
                    !tag.Contains("PrimaryButtonStyle", StringComparison.Ordinal))
                {
                    failures.Add($"저장 역할: {location}");
                }

                if ((content.Contains("인쇄", StringComparison.Ordinal) ||
                     content.Contains("엑셀 저장", StringComparison.Ordinal) ||
                     content.Contains("PDF 저장", StringComparison.Ordinal) ||
                     content.Contains("XPS", StringComparison.Ordinal)) &&
                    !tag.Contains("TradePlanPrintButtonStyle", StringComparison.Ordinal))
                {
                    failures.Add($"출력 역할: {location}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void RemainingExplicitButtonColors_AreLimitedToStateOrSelectionControls()
    {
        var root = FindRepositoryRoot();
        var appRoot = Path.Combine(root, "Desktop", "거래플랜.Desktop.App");
        var remaining = new List<string>();

        foreach (var xamlPath in EnumerateWindowXamlFiles(appRoot))
        {
            var xaml = File.ReadAllText(xamlPath);
            foreach (Match match in Regex.Matches(
                         xaml,
                         "<Button\\b[^>]*>",
                         RegexOptions.CultureInvariant | RegexOptions.Singleline))
            {
                var tag = match.Value;
                if (tag.Contains("Background=", StringComparison.Ordinal) &&
                    !tag.Contains("TradePlan", StringComparison.Ordinal))
                {
                    remaining.Add($"{Path.GetFileName(xamlPath)}:{GetLineNumber(xaml, match.Index)}");
                }
            }
        }

        Assert.Equal(
            new[]
            {
                "InventoryWindow.xaml:238",
                "InventoryWindow.xaml:243",
                "InventoryWindow.xaml:248",
                "PaymentWindow.xaml:239",
                "RentalBillingWindow.xaml:322",
                "SalesWindow.xaml:304"
            },
            remaining);
    }

    private static IEnumerable<string> EnumerateWindowXamlFiles(string appRoot)
    {
        yield return Path.Combine(appRoot, "MainWindow.xaml");

        foreach (var xamlPath in Directory.EnumerateFiles(
                     Path.Combine(appRoot, "Views"),
                     "*.xaml",
                     SearchOption.AllDirectories))
        {
            if (!xamlPath.Contains(
                    $"{Path.DirectorySeparatorChar}Backup{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                yield return xamlPath;
            }
        }
    }

    private static int GetLineNumber(string source, int index)
        => source[..index].Count(ch => ch == '\n') + 1;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "거래플랜.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜.sln을 찾을 수 없습니다.");
    }
}
