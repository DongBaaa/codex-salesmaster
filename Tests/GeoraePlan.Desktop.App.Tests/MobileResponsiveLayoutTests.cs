using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobileResponsiveLayoutTests
{
    private static readonly string ProjectRoot = FindProjectRoot();
    private static readonly string MobileRoot = Path.Combine(
        ProjectRoot,
        "Mobile",
        "GeoraePlan.Mobile.App");

    [Fact]
    public void MobileTheme_UsesMinimumSizesAndWrappingTextInsteadOfClippingControls()
    {
        var source = ReadMobileSource("Theme", "GeoraePlanTheme.cs");

        Assert.DoesNotContain("button.HeightRequest =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("entry.HeightRequest =", source, StringComparison.Ordinal);
        Assert.DoesNotContain("picker.HeightRequest =", source, StringComparison.Ordinal);
        Assert.Contains("MinimumHeightRequest = 44", source, StringComparison.Ordinal);
        Assert.Contains("MinimumWidthRequest = 0", source, StringComparison.Ordinal);
        Assert.True(
            CountOccurrences(source, "LineBreakMode = LineBreakMode.WordWrap") >= 4,
            "Every shared label factory must preserve multi-line text instead of clipping it.");
        Assert.Contains("CreateWrappingActions", source, StringComparison.Ordinal);
        Assert.Contains("FlexWrap.Wrap", source, StringComparison.Ordinal);
        Assert.Contains("CreateHorizontalActionScroller", source, StringComparison.Ordinal);
        Assert.Contains(
            "grid.Add(CreateHorizontalActionScroller(actions), 0, 1);",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MobileLongForms_AreVerticallyScrollable()
    {
        foreach (var page in new[]
                 {
                     "LoginPage.cs",
                     "HomePage.cs",
                     "CustomerEditPage.cs",
                     "ItemEditPage.cs",
                     "ItemsPage.cs",
                     "InvoiceDraftPage.cs",
                     "PaymentDraftPage.cs",
                     "InventoryTransfersPage.cs",
                     "RentalsPage.cs",
                     "SettingsPage.cs",
                     "SyncPage.cs",
                     "IntegrityReportPage.cs",
                     "UpdateRequiredPage.cs"
                 })
        {
            var source = ReadMobileSource("Pages", page);
            Assert.Contains(
                "new ScrollView",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void MobilePrimaryActions_StackOrWrapOnNarrowScreens()
    {
        var login = ReadMobileSource("Pages", "LoginPage.cs");
        var home = ReadMobileSource("Pages", "HomePage.cs");
        var customers = ReadMobileSource("Pages", "CustomersPage.cs");
        var invoices = ReadMobileSource("Pages", "InvoicesPage.cs");
        var items = ReadMobileSource("Pages", "ItemsPage.cs");
        var draft = ReadMobileSource("Pages", "InvoiceDraftPage.cs");
        var recycleBin = ReadMobileSource("Pages", "RecycleBinPage.cs");

        Assert.Contains("CreateWrappingActions(", login, StringComparison.Ordinal);
        Assert.Contains(
            "NavigationPage.SetHasNavigationBar(this, false)",
            login,
            StringComparison.Ordinal);
        Assert.Contains("var quickActionGrid = new VerticalStackLayout", home, StringComparison.Ordinal);
        Assert.True(CountOccurrences(customers, "CreateStackedActionLayout(") >= 1);
        Assert.True(CountOccurrences(customers, "CreateHorizontalActionScroller(") >= 2);
        Assert.True(CountOccurrences(invoices, "CreateStackedActionLayout(") >= 1);
        Assert.Contains("CreateWrappingActions(", invoices, StringComparison.Ordinal);
        Assert.Contains("headerScrollView.MaximumHeightRequest", invoices, StringComparison.Ordinal);
        Assert.True(CountOccurrences(items, "CreateStackedActionLayout(") >= 1);
        Assert.True(CountOccurrences(draft, "CreateStackedActionLayout(") >= 2);
        Assert.Contains("Orientation = ScrollOrientation.Horizontal", draft, StringComparison.Ordinal);
        Assert.Contains("headerScrollView.MaximumHeightRequest", recycleBin, StringComparison.Ordinal);
        Assert.DoesNotMatch(
            new Regex(@"(?<!Maximum)WidthRequest\s*=\s*210", RegexOptions.CultureInvariant),
            draft);
    }

    [Fact]
    public void MobilePages_DoNotUseLargeFixedControlHeights()
    {
        var pageRoot = Path.Combine(MobileRoot, "Pages");
        var fixedHeight = new Regex(
            @"\bHeightRequest\s*=\s*(?<height>\d+(?:\.\d+)?)",
            RegexOptions.CultureInvariant);

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(pageRoot, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var source = File.ReadAllText(file, Encoding.UTF8);
            foreach (Match match in fixedHeight.Matches(source))
            {
                var height = double.Parse(
                    match.Groups["height"].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                if (height >= 36)
                    violations.Add($"{Path.GetFileName(file)}:{match.Value}");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void MobileUserFacingText_NeverUsesEllipsisLineCapsOrFixedMaximumWidths()
    {
        var violations = new List<string>();
        var forbidden = new Regex(
            @"LineBreakMode\s*=\s*LineBreakMode\.(?:HeadTruncation|MiddleTruncation|TailTruncation|NoWrap)|\bMaxLines\s*=|\bMaximumWidthRequest\s*=",
            RegexOptions.CultureInvariant);

        foreach (var file in Directory.EnumerateFiles(
                     MobileRoot,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var source = File.ReadAllText(file, Encoding.UTF8);
            foreach (Match match in forbidden.Matches(source))
                violations.Add($"{Path.GetRelativePath(MobileRoot, file)}:{match.Value}");
        }

        Assert.Empty(violations);

        var app = ReadMobileSource("App.cs");
        Assert.Contains("RegisterGlobalFullTextStyles();", app, StringComparison.Ordinal);
        Assert.Contains("Label.LineBreakModeProperty", app, StringComparison.Ordinal);
        Assert.Contains("Button.LineBreakModeProperty", app, StringComparison.Ordinal);
        Assert.Contains("LineBreakMode.WordWrap", app, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileShell_KeepsFivePrimaryKoreanTabsAndOpensSettingsFromSync()
    {
        var shell = ReadMobileSource("AppShell.cs");
        var sync = ReadMobileSource("Pages", "SyncPage.cs");

        Assert.Equal(
            5,
            Regex.Matches(
                shell,
                "CreateTab<\\w+Page>\\(\"",
                RegexOptions.CultureInvariant).Count);
        Assert.Contains("CreateTab<SyncPage>(\"동기화\")", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateTab<SettingsPage>", shell, StringComparison.Ordinal);
        Assert.Contains("CreateButton(\"설정 열기\"", sync, StringComparison.Ordinal);
        Assert.Contains("Navigation.PushAsync(new SettingsPage())", sync, StringComparison.Ordinal);
    }

    private static string ReadMobileSource(params string[] parts)
        => File.ReadAllText(
            Path.Combine(new[] { MobileRoot }.Concat(parts).ToArray()),
            Encoding.UTF8);

    private static int CountOccurrences(string source, string value)
        => source.Split(value, StringSplitOptions.None).Length - 1;

    private static string FindProjectRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "거래플랜.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜 project root was not found.");
    }
}
