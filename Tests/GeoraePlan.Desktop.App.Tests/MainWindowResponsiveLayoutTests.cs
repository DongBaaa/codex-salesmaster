using System.Xml.Linq;
using System.Windows;
using 거래플랜.Desktop.App.Infrastructure;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MainWindowResponsiveLayoutTests
{
    [Theory]
    [InlineData(1920, 1040, 1.00)]
    [InlineData(1920, 1040, 1.25)]
    [InlineData(1920, 1040, 1.50)]
    [InlineData(1366, 728, 1.00)]
    [InlineData(1366, 728, 1.25)]
    [InlineData(1366, 728, 1.50)]
    [InlineData(1366, 728, 1.75)]
    [InlineData(1366, 728, 2.00)]
    [InlineData(656, 336, 1.00)]
    public void InitialWindowSize_FitsTargetWorkArea(
        double physicalWidth,
        double physicalHeight,
        double scale)
    {
        var logicalWorkArea = new Rect(
            0,
            0,
            physicalWidth / scale,
            physicalHeight / scale);

        var result =
            MainWindowResponsiveLayoutPolicy.ResolveInitialWindowSize(
                logicalWorkArea);

        Assert.InRange(
            result.Width,
            MainWindowResponsiveLayoutPolicy.MinimumWidthDip,
            logicalWorkArea.Width);
        Assert.InRange(
            result.Height,
            MainWindowResponsiveLayoutPolicy.MinimumHeightDip,
            logicalWorkArea.Height);
        Assert.True(
            result.Width <=
            logicalWorkArea.Width -
            MainWindowResponsiveLayoutPolicy.WorkAreaInsetDip +
            0.001);
        Assert.True(
            result.Height <=
            logicalWorkArea.Height -
            MainWindowResponsiveLayoutPolicy.WorkAreaInsetDip +
            0.001);
        Assert.True(
            result.Width <=
            MainWindowResponsiveLayoutPolicy.PreferredWidthDip);
        Assert.True(
            result.Height <=
            MainWindowResponsiveLayoutPolicy.PreferredHeightDip);
    }

    [Fact]
    public void InitialWindowBounds_CentersInsideOffsetMonitorWorkArea()
    {
        var workArea = new Rect(-1280, 40, 1280, 680);

        var result =
            MainWindowResponsiveLayoutPolicy.ResolveInitialWindowBounds(
                workArea);

        Assert.Equal(
            workArea.Left +
            ((workArea.Width - result.Width) / 2d),
            result.Left,
            precision: 6);
        Assert.Equal(
            workArea.Top +
            ((workArea.Height - result.Height) / 2d),
            result.Top,
            precision: 6);
        Assert.True(result.Left >= workArea.Left);
        Assert.True(result.Top >= workArea.Top);
        Assert.True(result.Right <= workArea.Right);
        Assert.True(result.Bottom <= workArea.Bottom);

    }

    [Theory]
    [InlineData(1920, 0, 1920, 1040, 1.50)]
    [InlineData(-2560, 0, 2560, 1440, 1.25)]
    [InlineData(0, 0, 1366, 728, 1.25)]
    [InlineData(0, 0, 1366, 728, 1.50)]
    [InlineData(0, 0, 1366, 728, 1.75)]
    [InlineData(0, 0, 1366, 728, 2.00)]
    [InlineData(0, 0, 656, 336, 1.00)]
    public void PhysicalWindowBounds_CentersInsideMixedDpiMonitor(
        double left,
        double top,
        double width,
        double height,
        double scale)
    {
        var workArea = new Rect(left, top, width, height);

        var result =
            MainWindowResponsiveLayoutPolicy.ResolvePhysicalWindowBounds(
                workArea,
                scale);

        Assert.False(result.IsEmpty);
        Assert.Equal(
            workArea.Left +
            ((workArea.Width - result.Width) / 2d),
            result.Left,
            precision: 6);
        Assert.Equal(
            workArea.Top +
            ((workArea.Height - result.Height) / 2d),
            result.Top,
            precision: 6);
        Assert.True(result.Left >= workArea.Left);
        Assert.True(result.Top >= workArea.Top);
        Assert.True(result.Right <= workArea.Right);
        Assert.True(result.Bottom <= workArea.Bottom);

        var expectedLogicalSize =
            MainWindowResponsiveLayoutPolicy.ResolveInitialWindowSize(
                new Rect(
                    0,
                    0,
                    workArea.Width / scale,
                    workArea.Height / scale));
        Assert.Equal(
            Math.Round(expectedLogicalSize.Width * scale),
            result.Width,
            precision: 6);
        Assert.Equal(
            Math.Round(expectedLogicalSize.Height * scale),
            result.Height,
            precision: 6);
    }

    [Theory]
    [InlineData(1200, 600, false)]
    [InlineData(1200, 599.99, true)]
    [InlineData(1092.8, 526.4, true)]
    [InlineData(894.7, 429.3, true)]
    public void CompactLayout_UsesClientHeightThreshold(
        double clientWidth,
        double clientHeight,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindowResponsiveLayoutPolicy.ShouldUseCompactLayout(
                new Size(clientWidth, clientHeight)));
    }

    [Theory]
    [InlineData(744, 400, false)]
    [InlineData(743.99, 400, true)]
    [InlineData(744, 399.99, true)]
    [InlineData(651, 308, true)]
    [InlineData(1200, 700, false)]
    public void ContentScrollFallback_ProtectsDenseWorkspace(
        double clientWidth,
        double clientHeight,
        bool expected)
    {
        Assert.Equal(
            expected,
            MainWindowResponsiveLayoutPolicy
                .ShouldUseContentScrollFallback(
                    new Size(clientWidth, clientHeight)));
    }

    [Fact]
    public void InvalidWorkArea_UsesPreferredSize()
    {
        var result =
            MainWindowResponsiveLayoutPolicy.ResolveInitialWindowSize(
                Rect.Empty);

        Assert.Equal(
            MainWindowResponsiveLayoutPolicy.PreferredWidthDip,
            result.Width);
        Assert.Equal(
            MainWindowResponsiveLayoutPolicy.PreferredHeightDip,
            result.Height);
    }

    [Fact]
    public void MainWindowXaml_KeepsResponsiveNavigationAndWorkspaceContracts()
    {
        var root = FindRepositoryRoot();
        var desktopAppDir = Directory
            .GetDirectories(Path.Combine(root, "Desktop"), "*.Desktop.App")
            .Single();
        var source = File.ReadAllText(
            Path.Combine(desktopAppDir, "MainWindow.xaml"));
        var document = XDocument.Parse(source);
        var window = Assert.IsType<XElement>(document.Root);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Null(window.Attribute("Width"));
        Assert.Null(window.Attribute("Height"));
        Assert.Equal(
            MainWindowResponsiveLayoutPolicy.MinimumWidthDip.ToString("0"),
            (string?)window.Attribute("MinWidth"));
        Assert.Equal(
            MainWindowResponsiveLayoutPolicy.MinimumHeightDip.ToString("0"),
            (string?)window.Attribute("MinHeight"));

        var rootScrollViewer = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "ScrollViewer" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "MainRootScrollViewer",
                    StringComparison.Ordinal));
        Assert.Equal(
            "Disabled",
            (string?)rootScrollViewer.Attribute(
                "HorizontalScrollBarVisibility"));
        Assert.Equal(
            "Disabled",
            (string?)rootScrollViewer.Attribute(
                "VerticalScrollBarVisibility"));
        Assert.Equal(
            "MainRootScrollViewer_SizeChanged",
            (string?)rootScrollViewer.Attribute("SizeChanged"));

        var rootPanel = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "DockPanel" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "MainRootPanel",
                    StringComparison.Ordinal));
        Assert.Equal(
            MainWindowResponsiveLayoutPolicy
                .MinimumContentWidthDip
                .ToString("0"),
            (string?)rootPanel.Attribute("MinWidth"));
        Assert.Equal(
            MainWindowResponsiveLayoutPolicy
                .MinimumContentHeightDip
                .ToString("0"),
            (string?)rootPanel.Attribute("MinHeight"));
        Assert.Null(rootPanel.Attribute("SizeChanged"));

        var navigation = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "WrapPanel" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "MainNavigationPanel",
                    StringComparison.Ordinal));
        Assert.Equal("1", (string?)navigation.Attribute("Grid.Column"));
        Assert.Equal(
            "Right",
            (string?)navigation.Attribute("HorizontalAlignment"));
        Assert.Contains(
            navigation.Elements(),
            element =>
                element.Name.LocalName == "Button" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "CompactDashboardToggleButton",
                    StringComparison.Ordinal));
        Assert.Contains(
            navigation.Descendants(),
            element =>
                element.Name.LocalName == "Button" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "CompactDesktopUpdateMenuButton",
                    StringComparison.Ordinal));
        Assert.Contains(
            navigation.Descendants(),
            element =>
                element.Name.LocalName == "MenuItem" &&
                string.Equals(
                    (string?)element.Attribute("Command"),
                    "{Binding StartPreparedDesktopUpdateCommand}",
                    StringComparison.Ordinal));
        Assert.Contains(
            navigation.Descendants(),
            element =>
                element.Name.LocalName == "MenuItem" &&
                string.Equals(
                    (string?)element.Attribute("Command"),
                    "{Binding DismissDesktopUpdateBannerCommand}",
                    StringComparison.Ordinal));

        var workspace = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Grid" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "MainWorkspaceGrid",
                    StringComparison.Ordinal));
        var columns = workspace
            .Elements()
            .Single(element =>
                element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .ToArray();
        Assert.Collection(
            columns,
            left =>
            {
                Assert.Equal("2*", (string?)left.Attribute("Width"));
                Assert.Equal("200", (string?)left.Attribute("MinWidth"));
                Assert.Equal("280", (string?)left.Attribute("MaxWidth"));
            },
            splitter =>
                Assert.Equal("8", (string?)splitter.Attribute("Width")),
            center =>
            {
                Assert.Equal("5*", (string?)center.Attribute("Width"));
                Assert.Equal("240", (string?)center.Attribute("MinWidth"));
            },
            splitter =>
                Assert.Equal("8", (string?)splitter.Attribute("Width")),
            right =>
            {
                Assert.Equal("2.6*", (string?)right.Attribute("Width"));
                Assert.Equal("260", (string?)right.Attribute("MinWidth"));
                Assert.Equal("360", (string?)right.Attribute("MaxWidth"));
            });

        var alertPanel = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Border" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "DashboardContractAlertsPanel",
                    StringComparison.Ordinal));
        Assert.Equal("154", (string?)alertPanel.Attribute("MaxHeight"));
        Assert.Contains(
            alertPanel.Descendants(),
            element =>
                element.Name.LocalName == "ScrollViewer" &&
                string.Equals(
                    (string?)element.Attribute(
                        "VerticalScrollBarVisibility"),
                    "Auto",
                    StringComparison.Ordinal));

        var summaryPanel = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Border" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "DashboardSummaryPanel",
                    StringComparison.Ordinal));
        Assert.NotNull(summaryPanel);

        var filterScrollViewer = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "ScrollViewer" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "InvoiceFilterScrollViewer",
                    StringComparison.Ordinal));
        Assert.Equal(
            "Auto",
            (string?)filterScrollViewer.Attribute(
                "HorizontalScrollBarVisibility"));
        Assert.Equal(
            "Disabled",
            (string?)filterScrollViewer.Attribute(
                "VerticalScrollBarVisibility"));

        var compactDashboardPopup = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Popup" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "CompactDashboardPopup",
                    StringComparison.Ordinal));
        Assert.Equal(
            "True",
            (string?)compactDashboardPopup.Attribute("StaysOpen"));
        Assert.Equal(
            "CompactDashboardPopup_Opened",
            (string?)compactDashboardPopup.Attribute("Opened"));
        Assert.Equal(
            "CompactDashboardPopup_Closed",
            (string?)compactDashboardPopup.Attribute("Closed"));
        var compactDashboardPopupPanel = Assert.Single(
            compactDashboardPopup.Descendants(),
            element =>
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "CompactDashboardPopupPanel",
                    StringComparison.Ordinal));
        Assert.Equal(
            "Cycle",
            (string?)compactDashboardPopupPanel.Attribute(
                "KeyboardNavigation.TabNavigation"));
        Assert.Equal(
            "CompactDashboardPopupPanel_PreviewKeyDown",
            (string?)compactDashboardPopupPanel.Attribute(
                "PreviewKeyDown"));
        Assert.Contains(
            compactDashboardPopup.Descendants(),
            element =>
                string.Equals(
                    (string?)element.Attribute("Command"),
                    "{Binding OpenDashboardReceivableDetailsCommand}",
                    StringComparison.Ordinal));
        Assert.Contains(
            compactDashboardPopup.Descendants(),
            element =>
                string.Equals(
                    (string?)element.Attribute("Command"),
                    "{Binding OpenDashboardPayableDetailsCommand}",
                    StringComparison.Ordinal));
        Assert.Contains(
            compactDashboardPopup.Descendants(),
            element =>
                string.Equals(
                    (string?)element.Attribute("Text"),
                    "{Binding DashboardMonthlyInvoiceCount, StringFormat={}{0:N0}건}",
                    StringComparison.Ordinal));
        Assert.Contains(
            compactDashboardPopup.Descendants(),
            element =>
                string.Equals(
                    (string?)element.Attribute("ItemsSource"),
                    "{Binding DashboardContractAlerts}",
                    StringComparison.Ordinal));
        Assert.Contains(
            compactDashboardPopup.Descendants(),
            element =>
                ((string?)element.Attribute("Text"))?.Contains(
                    "DashboardRentalUpcomingCount",
                    StringComparison.Ordinal) == true);
        Assert.Contains(
            compactDashboardPopup.Descendants(),
            element =>
                ((string?)element.Attribute("Text"))?.Contains(
                    "DashboardRentalOverdueCount",
                    StringComparison.Ordinal) == true);

        var policySource = File.ReadAllText(
            Path.Combine(
                desktopAppDir,
                "Infrastructure",
                "MainWindowResponsiveLayoutPolicy.cs"));
        Assert.Contains(
            "window.SourceInitialized += sourceInitializedHandler;",
            policySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MonitorFromWindow(",
            policySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MonitorFromPoint(",
            policySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetCursorPos(",
            policySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetDpiForWindow(",
            policySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "GetDpiForMonitor(",
            policySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "TryGetMonitorDpi(monitor",
            policySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SetWindowPos(",
            policySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolvePhysicalWindowBounds(",
            policySource,
            StringComparison.Ordinal);

        var codeBehindSource = File.ReadAllText(
            Path.Combine(desktopAppDir, "MainWindow.xaml.cs"));
        Assert.Contains(
            "MainWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(this);",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyResponsiveLayoutForAudit",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompactDashboardToggleButton_Click",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MainRootScrollViewer_SizeChanged",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShouldUseContentScrollFallback",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MainRootPanel.Width =",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "MainRootPanel.Height =",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            ".MinimumContentWidthDip)",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            ".MinimumContentHeightDip)",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CurrentUserDisplayText.Visibility",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CurrentUserSeparator.Visibility",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompactDashboardPopupPanel_PreviewKeyDown",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "e.Key != Key.Escape",
            codeBehindSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "Keyboard.Focus(",
            codeBehindSource,
            StringComparison.Ordinal);

        var projectPath = Directory
            .GetFiles(desktopAppDir, "*.csproj")
            .Single();
        var projectDocument = XDocument.Load(projectPath);
        Assert.Contains(
            projectDocument.Descendants(),
            element =>
                element.Name.LocalName == "ApplicationManifest" &&
                string.Equals(
                    element.Value.Trim(),
                    "app.manifest",
                    StringComparison.Ordinal));

        var manifestPath = Path.Combine(
            desktopAppDir,
            "app.manifest");
        var manifestDocument = XDocument.Load(manifestPath);
        Assert.Contains(
            manifestDocument.Descendants(),
            element =>
                element.Name.LocalName == "dpiAwareness" &&
                element.Value.Contains(
                    "PerMonitorV2",
                    StringComparison.Ordinal));
        Assert.Contains(
            manifestDocument.Descendants(),
            element =>
                element.Name.LocalName == "requestedExecutionLevel" &&
                string.Equals(
                    (string?)element.Attribute("level"),
                    "asInvoker",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void DpiRuntimeAudit_CoversRequiredProfilesAndProtectsEvidence()
    {
        var root = FindRepositoryRoot();
        var auditProject = Path.Combine(
            root,
            "tasks",
            "MainWindowDpiRuntimeAudit",
            "MainWindowDpiRuntimeAudit.csproj");
        var auditSourcePath = Path.Combine(
            root,
            "tasks",
            "MainWindowDpiRuntimeAudit",
            "Program.cs");

        Assert.True(File.Exists(auditProject));
        var source = File.ReadAllText(auditSourcePath);

        Assert.Contains(
            "new(\"fhd-100\", 1920, 1040, 96)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"fhd-125\", 1920, 1040, 120)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"fhd-150\", 1920, 1040, 144)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"minimum-window-776x456-100\", 776, 456, 96)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"window-minimum-656x336-100\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"window-minimum-656x336-100-update\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"minimum-window-776x456-100-update\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"low-resolution-1366x768-100\", 1366, 728, 96)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"low-resolution-1366x768-125\", 1366, 728, 120)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "new(\"low-resolution-1366x768-150\", 1366, 728, 144)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"low-resolution-1366x768-150-update\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"low-resolution-1366x768-175\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"low-resolution-1366x768-175-update\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"low-resolution-1366x768-200\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"low-resolution-1366x768-200-update\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "NavigationOverlapPairs",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "WithinClientBounds",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Evidence directory must be new or empty",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DesktopAppDllSha256",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "MainWindowXamlSha256",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "EmbeddedManifestSha256",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PerMonitorV2",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "LayoutBuildFresh",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "EnsurePathIsInsideRepository",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompactDesktopUpdateMenuButton",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RootScrollFallbackStateMatches",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContentExtentWidthDip",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScrollToRightEnd",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ScrollToBottom",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "RequiredActionsBroughtIntoViewport",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "scroll-end.png",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AuditProgramSha256",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AuditAssemblySha256",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AuditBuildFresh",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "GitHead",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyResponsiveLayoutForAudit",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "물리 모니터 촬영이 아니며",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "WM_DPICHANGED",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Popup·ContextMenu 입력은 이 감사에서 실행하지 않습니다.",
            source,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")) &&
                Directory.GetFiles(current.FullName, "*.sln").Length > 0)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
