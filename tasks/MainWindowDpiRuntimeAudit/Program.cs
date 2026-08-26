using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using 거래플랜.Desktop.App;
using 거래플랜.Desktop.App.Infrastructure;

internal static class Program
{
    private const double NonClientWidthAllowanceDip = 16d;
    private const double NonClientHeightAllowanceDip = 40d;
    private const double BoundsToleranceDip = 0.75d;
    private const double MinimumWorkspaceHeightDip = 180d;
    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const int ResourceTypeManifest = 24;

    private static readonly string[] RequiredButtonNames =
    [
        "품목/재고 관리",
        "신규 렌탈 등록",
        "거래처 관리",
        "매입/매출 장부",
        "기간별 집계",
        "렌탈 업무",
        "환경설정",
        "휴지통",
        "로그아웃",
        "판매작성",
        "구매작성",
        "수금 입력",
        "전표 인쇄[F9]"
    ];

    private static readonly string[] NavigationButtonNames =
    [
        "품목/재고 관리",
        "신규 렌탈 등록",
        "거래처 관리",
        "매입/매출 장부",
        "기간별 집계",
        "렌탈 업무",
        "환경설정",
        "휴지통",
        "로그아웃"
    ];

    private static readonly LayoutProfile[] Profiles =
    [
        new("fhd-100", 1920, 1040, 96),
        new("fhd-125", 1920, 1040, 120),
        new("fhd-150", 1920, 1040, 144),
        new(
            "window-minimum-656x336-100",
            656,
            336,
            96,
            AllowRootScrolling: true),
        new(
            "window-minimum-656x336-100-update",
            656,
            336,
            96,
            ShowUpdateBanner: true,
            AllowRootScrolling: true),
        new(
            "minimum-window-776x456-100",
            776,
            456,
            96,
            AllowRootScrolling: true),
        new(
            "minimum-window-776x456-100-update",
            776,
            456,
            96,
            ShowUpdateBanner: true,
            AllowRootScrolling: true),
        new("low-resolution-1366x768-100", 1366, 728, 96),
        new(
            "low-resolution-1366x768-125",
            1366,
            728,
            120,
            AllowRootScrolling: true),
        new(
            "low-resolution-1366x768-150",
            1366,
            728,
            144,
            AllowRootScrolling: true),
        new(
            "low-resolution-1366x768-150-update",
            1366,
            728,
            144,
            ShowUpdateBanner: true,
            AllowRootScrolling: true),
        new(
            "low-resolution-1366x768-175",
            1366,
            728,
            168,
            AllowRootScrolling: true),
        new(
            "low-resolution-1366x768-175-update",
            1366,
            728,
            168,
            ShowUpdateBanner: true,
            AllowRootScrolling: true),
        new(
            "low-resolution-1366x768-200",
            1366,
            728,
            192,
            AllowRootScrolling: true),
        new(
            "low-resolution-1366x768-200-update",
            1366,
            728,
            192,
            ShowUpdateBanner: true,
            AllowRootScrolling: true)
    ];

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var evidenceDirectory = ResolveEvidenceDirectory(args);
            var provenance = CollectSourceProvenance();
            PrepareEvidenceDirectory(evidenceDirectory);

            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_TEST_MODE",
                "1",
                EnvironmentVariableTarget.Process);

            var app = new App
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown
            };
            app.InitializeComponent();

            var shutdownPopupResult =
                AuditShutdownActivityPopup(evidenceDirectory);

            var results = Profiles
                .Select(
                    profile =>
                    {
                        var mainWindow =
                            CreateLayoutOnlyMainWindow();
                        var root =
                            mainWindow.Content as FrameworkElement
                            ?? throw new InvalidOperationException(
                                "MainWindow root content was not a FrameworkElement.");
                        mainWindow.Content = null;
                        return AuditProfile(
                            mainWindow,
                            root,
                            profile,
                            evidenceDirectory);
                    })
                .ToArray();

            var passed =
                results.All(result => result.Passed) &&
                shutdownPopupResult.Passed;
            var payload = new AuditPayload(
                DateTimeOffset.Now,
                passed ? "PASS" : "FAIL",
                "실제 MainWindow XAML/BAML visual tree를 target logical work area로 Measure/Arrange하고 target DPI RenderTargetBitmap으로 캡처한 결정적 offscreen audit입니다. 물리 모니터 촬영이 아니며 SourceInitialized/HWND 배치와 WM_DPICHANGED는 실행하지 않습니다. 종료 동기화 팝업은 같은 실행에서 제품 생성 함수를 직접 호출해 별도 PNG·측정 보고서로 검증합니다.",
                provenance,
                NonClientWidthAllowanceDip,
                NonClientHeightAllowanceDip,
                results);

            var jsonPath = Path.Combine(
                evidenceDirectory,
                "main-window-dpi-runtime-audit.json");
            var markdownPath = Path.Combine(
                evidenceDirectory,
                "main-window-dpi-runtime-audit.md");

            File.WriteAllText(
                jsonPath,
                JsonSerializer.Serialize(
                    payload,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            File.WriteAllLines(
                markdownPath,
                BuildMarkdown(payload, jsonPath),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Console.WriteLine($"result={payload.Result}");
            Console.WriteLine($"json={jsonPath}");
            Console.WriteLine($"markdown={markdownPath}");
            Console.WriteLine(
                $"shutdown_popup_markdown={shutdownPopupResult.MarkdownPath}");
            return passed ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 2;
        }
    }

    private static MainWindow CreateLayoutOnlyMainWindow() =>
        new(
            vm: null!,
            local: null!,
            rental: null!,
            rentalDocuments: null!,
            print: null!,
            invoicePrintService: null!,
            session: null!,
            api: null!,
            sync: null!,
            backup: null!,
            diagnostics: null!,
            dataIntegrity: null!,
            serviceScopeFactory: null!);

    private static ShutdownPopupAuditResult AuditShutdownActivityPopup(
        string evidenceDirectory)
    {
        var owner = new Window
        {
            Width = 1000,
            Height = 700,
            Left = -20000,
            Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ShowActivated = false
        };

        Window? popup = null;
        try
        {
            owner.Show();
            var method = typeof(App).GetMethod(
                "ShowShutdownSavingPopup",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new MissingMethodException(
                    typeof(App).FullName,
                    "ShowShutdownSavingPopup");

            popup = method.Invoke(null, [owner]) as Window
                ?? throw new InvalidOperationException(
                    "ShowShutdownSavingPopup did not return a Window.");
            var content = popup.Content as FrameworkElement
                ?? throw new InvalidOperationException(
                    "Shutdown popup content was not a FrameworkElement.");
            var scrollViewer = content as ScrollViewer
                ?? throw new InvalidOperationException(
                    "Shutdown popup root was not a ScrollViewer.");
            var popupBody = scrollViewer.Content as FrameworkElement
                ?? throw new InvalidOperationException(
                    "Shutdown popup body was not a FrameworkElement.");
            var popupBorder = popupBody as Border
                ?? throw new InvalidOperationException(
                    "Shutdown popup body was not a Border.");
            var popupStack = popupBorder.Child as StackPanel
                ?? throw new InvalidOperationException(
                    "Shutdown popup stack was not a StackPanel.");
            var sizeToContent = popup.SizeToContent;
            var isGlobalLayoutExcluded =
                ResponsiveWindowBehavior.GetIsGlobalLayoutExcluded(popup);
            var responsiveBehaviorEnabled =
                ResponsiveWindowBehavior.GetIsEnabled(popup);
            popup.UpdateLayout();
            var actualWindowWidth = popup.ActualWidth;
            var actualWindowHeight = popup.ActualHeight;
            var actualBodyWidth = popupBorder.ActualWidth;
            var actualBodyHeight = popupBorder.ActualHeight;
            var actualWindowScreenshotPath = Path.Combine(
                evidenceDirectory,
                "shutdown-sync-popup-actual-window-runtime.png");
            CapturePng(
                scrollViewer,
                new Size(scrollViewer.ActualWidth, scrollViewer.ActualHeight),
                96,
                actualWindowScreenshotPath);
            popup.Content = null;
            popup.Close();
            popup = null;

            scrollViewer.Content = null;
            popupBorder.Child = null;
            var freshStack = new StackPanel
            {
                Orientation = popupStack.Orientation
            };
            while (popupStack.Children.Count > 0)
            {
                var child = popupStack.Children[0];
                popupStack.Children.RemoveAt(0);
                freshStack.Children.Add(child);
            }

            popupBody = new Border
            {
                Background = popupBorder.Background,
                BorderBrush = popupBorder.BorderBrush,
                BorderThickness = popupBorder.BorderThickness,
                CornerRadius = popupBorder.CornerRadius,
                Padding = popupBorder.Padding,
                Width = popupBorder.Width,
                Child = freshStack
            };
            InvalidateLayoutTree(popupBody);
            popupBody.Measure(
                new Size(
                    double.PositiveInfinity,
                    double.PositiveInfinity));
            var naturalSize = popupBody.DesiredSize;
            popupBody.Arrange(new Rect(naturalSize));
            popupBody.UpdateLayout();

            var contentWidth = popupBody.ActualWidth;
            var contentHeight = popupBody.ActualHeight;
            var actualBottomBlankSpace = Math.Max(
                0d,
                actualWindowHeight - contentHeight);
            var popupDpis = new[] { 96, 120, 144, 192 };
            var popupScreenshots = popupDpis
                .Select(
                    dpi =>
                    {
                        var scalePercent = (int)Math.Round(dpi / 96d * 100d);
                        var screenshotPath = Path.Combine(
                            evidenceDirectory,
                            dpi == 96
                                ? "shutdown-sync-popup-runtime.png"
                                : $"shutdown-sync-popup-runtime-{scalePercent}.png");
                        CapturePng(
                            popupBody,
                            new Size(contentWidth, contentHeight),
                            dpi,
                            screenshotPath);
                        return new PopupScreenshotEvidence(
                            scalePercent,
                            dpi,
                            screenshotPath,
                            (int)Math.Ceiling(contentWidth * dpi / 96d),
                            (int)Math.Ceiling(contentHeight * dpi / 96d),
                            ComputeSha256(screenshotPath));
                    })
                .ToArray();

            var passed =
                sizeToContent == SizeToContent.WidthAndHeight &&
                isGlobalLayoutExcluded &&
                !responsiveBehaviorEnabled &&
                contentWidth is >= 400d and <= 440d &&
                contentHeight is >= 110d and <= 165d &&
                actualWindowWidth is >= 400d and <= 440d &&
                actualWindowHeight is >= 110d and <= 165d &&
                actualBottomBlankSpace <= 1d &&
                File.Exists(actualWindowScreenshotPath) &&
                popupScreenshots.Length == popupDpis.Length &&
                popupScreenshots.All(
                    screenshot => File.Exists(screenshot.Path)) &&
                scrollViewer.ComputedHorizontalScrollBarVisibility ==
                    Visibility.Collapsed &&
                scrollViewer.ComputedVerticalScrollBarVisibility ==
                    Visibility.Collapsed;
            var markdownPath = Path.Combine(
                evidenceDirectory,
                "shutdown-sync-popup-runtime-audit.md");
            var markdownLines = new List<string>
            {
                "# 종료 동기화 팝업 WPF 런타임 감사",
                "",
                $"- 결과: `{(passed ? "PASS" : "FAIL")}`",
                "- evidence: 제품의 ShowShutdownSavingPopup을 직접 호출한 뒤 SizeToContent 자연 크기로 Measure/Arrange하고 100%·125%·150%·200% target DPI로 렌더링한 WPF 증거",
                $"- natural popup content size: `{contentWidth:N1}×{contentHeight:N1} DIP`",
                $"- actual window / body size: `{actualWindowWidth:N1}×{actualWindowHeight:N1}` / `{actualBodyWidth:N1}×{actualBodyHeight:N1} DIP`",
                $"- actual bottom blank space: `{actualBottomBlankSpace:N1} DIP`",
                $"- SizeToContent: `{sizeToContent}`",
                $"- global responsive layout excluded: `{isGlobalLayoutExcluded}`",
                $"- responsive behavior enabled: `{responsiveBehaviorEnabled}`",
                $"- horizontal scrollbar: `{scrollViewer.ComputedHorizontalScrollBarVisibility}`",
                $"- vertical scrollbar: `{scrollViewer.ComputedVerticalScrollBarVisibility}`",
                $"- actual window screenshot: `{actualWindowScreenshotPath}`",
                $"- actual window screenshot SHA-256: `{ComputeSha256(actualWindowScreenshotPath)}`",
                "",
                "## DPI render evidence"
            };
            markdownLines.AddRange(
                popupScreenshots.Select(
                    screenshot =>
                        $"- {screenshot.ScalePercent}% ({screenshot.Dpi} DPI): " +
                        $"`{screenshot.PixelWidth}×{screenshot.PixelHeight}px`, " +
                        $"`{screenshot.Path}`, SHA-256 `{screenshot.Sha256}`"));
            File.WriteAllLines(
                markdownPath,
                markdownLines,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            return new ShutdownPopupAuditResult(
                passed,
                markdownPath);
        }
        finally
        {
            popup?.Close();
            owner.Close();
        }
    }

    private static void InvalidateLayoutTree(DependencyObject root)
    {
        if (root is UIElement element)
        {
            element.InvalidateMeasure();
            element.InvalidateArrange();
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            InvalidateLayoutTree(
                VisualTreeHelper.GetChild(root, index));
        }
    }

    private static ProfileResult AuditProfile(
        MainWindow mainWindow,
        FrameworkElement root,
        LayoutProfile profile,
        string evidenceDirectory)
    {
        var scale = profile.Dpi / 96d;
        var workAreaDip = new Rect(
            0,
            0,
            profile.WorkAreaWidthPixels / scale,
            profile.WorkAreaHeightPixels / scale);
        var outerSize = ResolveInitialWindowSize(workAreaDip);
        var clientSize = new Size(
            Math.Max(
                1d,
                outerSize.Width - NonClientWidthAllowanceDip),
            Math.Max(
                1d,
                outerSize.Height - NonClientHeightAllowanceDip));

        root.DataContext = new AuditMainWindowDataContext(
            profile.ShowUpdateBanner);
        ApplyResponsiveLayout(mainWindow, clientSize);
        root.Measure(clientSize);
        root.Arrange(new Rect(new Point(0, 0), clientSize));
        root.UpdateLayout();

        var rootScrollViewer = FindNamedElement(
            root,
            "MainRootScrollViewer") as ScrollViewer
            ?? throw new InvalidOperationException(
                "Main root scroll fallback host was not a ScrollViewer.");
        rootScrollViewer.ScrollToHome();
        rootScrollViewer.ScrollToHorizontalOffset(0d);
        rootScrollViewer.ScrollToVerticalOffset(0d);
        root.UpdateLayout();
        var contentExtentSize = new Size(
            Math.Max(clientSize.Width, rootScrollViewer.ExtentWidth),
            Math.Max(clientSize.Height, rootScrollViewer.ExtentHeight));
        var rootScrollFallbackExpected =
            ResolveContentScrollFallback(clientSize);
        var rootScrollFallbackExpectationMatches =
            rootScrollFallbackExpected ==
            profile.AllowRootScrolling;
        var rootScrollFallbackApplied =
            rootScrollViewer.HorizontalScrollBarVisibility ==
            ScrollBarVisibility.Auto &&
            rootScrollViewer.VerticalScrollBarVisibility ==
            ScrollBarVisibility.Auto &&
            (rootScrollViewer.ScrollableWidth >
             BoundsToleranceDip ||
             rootScrollViewer.ScrollableHeight >
             BoundsToleranceDip);
        var rootScrollFallbackDisabled =
            rootScrollViewer.HorizontalScrollBarVisibility ==
            ScrollBarVisibility.Disabled &&
            rootScrollViewer.VerticalScrollBarVisibility ==
            ScrollBarVisibility.Disabled &&
            rootScrollViewer.ScrollableWidth <=
            BoundsToleranceDip &&
            rootScrollViewer.ScrollableHeight <=
            BoundsToleranceDip;
        var rootScrollFallbackStateMatches =
            rootScrollFallbackExpectationMatches &&
            Math.Abs(rootScrollViewer.HorizontalOffset) <=
            BoundsToleranceDip &&
            Math.Abs(rootScrollViewer.VerticalOffset) <=
            BoundsToleranceDip &&
            (rootScrollFallbackExpected
                ? rootScrollFallbackApplied
                : rootScrollFallbackDisabled);

        var buttonMetrics = RequiredButtonNames
            .Select(name =>
                CollectButtonMetric(
                    root,
                    name,
                    clientSize,
                    contentExtentSize))
            .ToArray();
        var compactToggleButton = FindNamedElement(
            root,
            "CompactDashboardToggleButton");
        var compactToggleMetric = CollectButtonMetric(
            root,
            compactToggleButton as Button
                ?? throw new InvalidOperationException(
                    "Compact dashboard toggle was not a Button."),
            clientSize,
            contentExtentSize);
        var compactUpdateButton = FindNamedElement(
            root,
            "CompactDesktopUpdateMenuButton");
        var compactUpdateMetric = CollectButtonMetric(
            root,
            compactUpdateButton as Button
                ?? throw new InvalidOperationException(
                    "Compact desktop update menu was not a Button."),
            clientSize,
            contentExtentSize);
        var compactLayoutExpected =
            ResolveCompactLayout(clientSize);
        var navigationButtons = buttonMetrics
            .Where(metric =>
                NavigationButtonNames.Contains(
                    metric.Name,
                    StringComparer.Ordinal))
            .Concat(
                compactToggleButton.Visibility == Visibility.Visible
                    ? [compactToggleMetric]
                    : [])
            .Concat(
                compactLayoutExpected &&
                profile.ShowUpdateBanner
                    ? [compactUpdateMetric]
                    : [])
            .ToArray();
        var overlapPairs = FindOverlaps(navigationButtons);

        var navigationPanel = FindNamedElement(
            root,
            "MainNavigationPanel");
        var workspace = FindNamedElement(root, "MainWorkspaceGrid");
        var summaryPanel = FindNamedElement(
            root,
            "DashboardSummaryPanel");
        var alertPanel = FindNamedElement(
            root,
            "DashboardContractAlertsPanel");
        var filterPanel = FindNamedElement(
            root,
            "InvoiceFilterPanel");
        var desktopUpdateBannerHost = FindNamedElement(
            root,
            "DesktopUpdateBannerResponsiveHost");

        var navigationBounds = GetBounds(root, navigationPanel);
        var workspaceBounds = GetBounds(root, workspace);
        var summaryBounds = GetBounds(root, summaryPanel);
        var alertBounds = GetBounds(root, alertPanel);
        var filterPanelBounds = GetBounds(root, filterPanel);
        var compactLayoutApplied =
            compactToggleButton.Visibility == Visibility.Visible &&
            summaryPanel.Visibility == Visibility.Collapsed &&
            alertPanel.Visibility == Visibility.Collapsed;
        var normalLayoutApplied =
            compactToggleButton.Visibility == Visibility.Collapsed &&
            summaryPanel.Visibility == Visibility.Visible &&
            alertPanel.Visibility == Visibility.Visible;
        var responsiveStateMatches =
            compactLayoutExpected
                ? compactLayoutApplied
                : normalLayoutApplied;
        var updateSurfaceMatches =
            compactLayoutExpected
                ? desktopUpdateBannerHost.Visibility ==
                  Visibility.Collapsed &&
                  (!profile.ShowUpdateBanner ||
                   IsReachable(
                       compactUpdateMetric,
                       rootScrollFallbackExpected))
                : desktopUpdateBannerHost.Visibility ==
                  Visibility.Visible;
        var requiredBoundsSize =
            rootScrollFallbackExpected
                ? contentExtentSize
                : clientSize;
        var namedElementsWithinBounds =
            IsWithin(requiredBoundsSize, navigationBounds) &&
            IsWithin(requiredBoundsSize, workspaceBounds) &&
            IsWithin(requiredBoundsSize, filterPanelBounds) &&
            (compactLayoutExpected ||
             (IsWithin(requiredBoundsSize, summaryBounds) &&
              IsWithin(requiredBoundsSize, alertBounds))) &&
            (!compactLayoutExpected ||
             IsReachable(
                 compactToggleMetric,
                 rootScrollFallbackExpected));
        var workspaceUsable =
            workspaceBounds.Width >= 700d &&
            workspaceBounds.Height >= MinimumWorkspaceHeightDip;

        var scrollActionButtons = RequiredButtonNames
            .Select(name => FindButtonByContent(root, name))
            .Concat(
                compactToggleButton.Visibility == Visibility.Visible
                    ? [(Button)compactToggleButton]
                    : [])
            .Concat(
                compactLayoutExpected &&
                profile.ShowUpdateBanner &&
                compactUpdateButton.Visibility == Visibility.Visible
                    ? [(Button)compactUpdateButton]
                    : [])
            .ToArray();
        var scrollEndScreenshotPath =
            rootScrollFallbackExpected
                ? Path.Combine(
                    evidenceDirectory,
                    $"main-window-{profile.Name}-scroll-end.png")
                : null;
        var scrollReachability = VerifyScrollReachability(
            root,
            rootScrollViewer,
            clientSize,
            profile.Dpi,
            rootScrollFallbackExpected,
            scrollActionButtons,
            scrollEndScreenshotPath);

        var screenshotPath = Path.Combine(
            evidenceDirectory,
            $"main-window-{profile.Name}.png");
        CapturePng(
            root,
            clientSize,
            profile.Dpi,
            screenshotPath);
        var screenshotSha256 = ComputeSha256(screenshotPath);

        var passed =
            buttonMetrics.All(metric =>
                metric.Found &&
                metric.Width > 1d &&
                metric.Height > 1d &&
                IsReachable(
                    metric,
                    rootScrollFallbackExpected)) &&
            overlapPairs.Length == 0 &&
            namedElementsWithinBounds &&
            responsiveStateMatches &&
            updateSurfaceMatches &&
            rootScrollFallbackStateMatches &&
            scrollReachability.MaxOffsetsReached &&
            scrollReachability.RequiredActionsBroughtIntoViewport &&
            scrollReachability.OriginRestored &&
            workspaceUsable &&
            alertBounds.Height <= 154d + BoundsToleranceDip;

        return new ProfileResult(
            profile.Name,
            profile.WorkAreaWidthPixels,
            profile.WorkAreaHeightPixels,
            profile.Dpi,
            scale,
            outerSize.Width,
            outerSize.Height,
            clientSize.Width,
            clientSize.Height,
            workspaceBounds.Width,
            workspaceBounds.Height,
            alertBounds.Height,
            compactLayoutExpected,
            responsiveStateMatches,
            IsReachable(
                compactToggleMetric,
                rootScrollFallbackExpected),
            profile.ShowUpdateBanner,
            updateSurfaceMatches,
            rootScrollFallbackExpected,
            rootScrollFallbackStateMatches,
            rootScrollViewer.HorizontalScrollBarVisibility.ToString(),
            rootScrollViewer.VerticalScrollBarVisibility.ToString(),
            rootScrollViewer.ScrollableWidth,
            rootScrollViewer.ScrollableHeight,
            rootScrollViewer.ViewportWidth,
            rootScrollViewer.ViewportHeight,
            contentExtentSize.Width,
            contentExtentSize.Height,
            rootScrollViewer.HorizontalOffset,
            rootScrollViewer.VerticalOffset,
            scrollReachability.MaxOffsetsReached,
            scrollReachability.RequiredActionsBroughtIntoViewport,
            scrollReachability.OriginRestored,
            passed,
            overlapPairs,
            buttonMetrics,
            screenshotPath,
            screenshotSha256,
            scrollReachability.ScrollEndScreenshotPath,
            scrollReachability.ScrollEndScreenshotSha256);
    }

    private static void ApplyResponsiveLayout(
        MainWindow mainWindow,
        Size clientSize)
    {
        var method = typeof(MainWindow).GetMethod(
            "ApplyResponsiveLayoutForAudit",
            BindingFlags.Instance |
            BindingFlags.NonPublic |
            BindingFlags.Public)
            ?? throw new InvalidOperationException(
                "MainWindow responsive layout audit method was not found.");
        _ = method.Invoke(mainWindow, [clientSize]);
    }

    private static Size ResolveInitialWindowSize(Rect workArea)
    {
        var policyType = typeof(MainWindow).Assembly.GetType(
            "거래플랜.Desktop.App.Infrastructure.MainWindowResponsiveLayoutPolicy",
            throwOnError: true)
            ?? throw new InvalidOperationException(
                "MainWindow responsive layout policy type was not found.");
        var method = policyType.GetMethod(
            "ResolveInitialWindowSize",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MainWindow responsive layout policy method was not found.");

        return method.Invoke(null, [workArea]) is Size size
            ? size
            : throw new InvalidOperationException(
                "MainWindow responsive layout policy returned no size.");
    }

    private static bool ResolveCompactLayout(Size clientSize)
    {
        var policyType = typeof(MainWindow).Assembly.GetType(
            "거래플랜.Desktop.App.Infrastructure.MainWindowResponsiveLayoutPolicy",
            throwOnError: true)
            ?? throw new InvalidOperationException(
                "MainWindow responsive layout policy type was not found.");
        var method = policyType.GetMethod(
            "ShouldUseCompactLayout",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MainWindow compact layout policy method was not found.");

        return method.Invoke(null, [clientSize]) is bool result
            ? result
            : throw new InvalidOperationException(
                "MainWindow compact layout policy returned no result.");
    }

    private static bool ResolveContentScrollFallback(Size clientSize)
    {
        var policyType = typeof(MainWindow).Assembly.GetType(
            "거래플랜.Desktop.App.Infrastructure.MainWindowResponsiveLayoutPolicy",
            throwOnError: true)
            ?? throw new InvalidOperationException(
                "MainWindow responsive layout policy type was not found.");
        var method = policyType.GetMethod(
            "ShouldUseContentScrollFallback",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "MainWindow content scroll fallback policy method was not found.");

        return method.Invoke(null, [clientSize]) is bool result
            ? result
            : throw new InvalidOperationException(
                "MainWindow content scroll fallback policy returned no result.");
    }

    private static ButtonMetric CollectButtonMetric(
        FrameworkElement root,
        string name,
        Size clientSize,
        Size contentExtentSize)
    {
        var button = EnumerateVisualDescendants<Button>(root)
            .FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Content as string,
                    name,
                    StringComparison.Ordinal));
        if (button is null)
            return new ButtonMetric(
                name,
                false,
                0,
                0,
                0,
                0,
                false,
                false);

        var bounds = GetBounds(root, button);
        return new ButtonMetric(
            name,
            true,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            IsWithin(clientSize, bounds),
            IsWithin(contentExtentSize, bounds));
    }

    private static ButtonMetric CollectButtonMetric(
        FrameworkElement root,
        Button button,
        Size clientSize,
        Size contentExtentSize)
    {
        var bounds = GetBounds(root, button);
        return new ButtonMetric(
            button.Content as string ?? button.Name,
            true,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            IsWithin(clientSize, bounds),
            IsWithin(contentExtentSize, bounds));
    }

    private static bool IsReachable(
        ButtonMetric metric,
        bool rootScrollFallbackExpected) =>
        rootScrollFallbackExpected
            ? metric.WithinContentExtentBounds
            : metric.WithinClientBounds;

    private static Button FindButtonByContent(
        FrameworkElement root,
        string content) =>
        EnumerateVisualDescendants<Button>(root)
            .FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Content as string,
                    content,
                    StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"Required button was not found: {content}");

    private static ScrollReachabilityResult VerifyScrollReachability(
        FrameworkElement root,
        ScrollViewer scrollViewer,
        Size clientSize,
        int dpi,
        bool rootScrollFallbackExpected,
        IReadOnlyList<Button> actionButtons,
        string? scrollEndScreenshotPath)
    {
        if (!rootScrollFallbackExpected)
        {
            return new ScrollReachabilityResult(
                MaxOffsetsReached: true,
                RequiredActionsBroughtIntoViewport: true,
                OriginRestored:
                    Math.Abs(scrollViewer.HorizontalOffset) <=
                    BoundsToleranceDip &&
                    Math.Abs(scrollViewer.VerticalOffset) <=
                    BoundsToleranceDip,
                ScrollEndScreenshotPath: null,
                ScrollEndScreenshotSha256: null);
        }

        scrollViewer.ScrollToRightEnd();
        scrollViewer.ScrollToBottom();
        root.UpdateLayout();
        var maxOffsetsReached =
            Math.Abs(
                scrollViewer.HorizontalOffset -
                scrollViewer.ScrollableWidth) <=
            BoundsToleranceDip &&
            Math.Abs(
                scrollViewer.VerticalOffset -
                scrollViewer.ScrollableHeight) <=
            BoundsToleranceDip;

        string? scrollEndScreenshotSha256 = null;
        if (!string.IsNullOrWhiteSpace(scrollEndScreenshotPath))
        {
            CapturePng(
                root,
                clientSize,
                dpi,
                scrollEndScreenshotPath);
            scrollEndScreenshotSha256 =
                ComputeSha256(scrollEndScreenshotPath);
        }

        var viewportSize = new Size(
            scrollViewer.ViewportWidth,
            scrollViewer.ViewportHeight);
        var requiredActionsBroughtIntoViewport = true;
        foreach (var button in actionButtons)
        {
            button.BringIntoView(
                new Rect(
                    new Point(0, 0),
                    button.RenderSize));
            root.UpdateLayout();
            if (!IsWithin(
                    viewportSize,
                    GetBounds(root, button)))
            {
                requiredActionsBroughtIntoViewport = false;
                break;
            }
        }

        scrollViewer.ScrollToHorizontalOffset(0d);
        scrollViewer.ScrollToVerticalOffset(0d);
        root.UpdateLayout();
        var originRestored =
            Math.Abs(scrollViewer.HorizontalOffset) <=
            BoundsToleranceDip &&
            Math.Abs(scrollViewer.VerticalOffset) <=
            BoundsToleranceDip;

        return new ScrollReachabilityResult(
            maxOffsetsReached,
            requiredActionsBroughtIntoViewport,
            originRestored,
            scrollEndScreenshotPath,
            scrollEndScreenshotSha256);
    }

    private static string[] FindOverlaps(
        IReadOnlyList<ButtonMetric> metrics)
    {
        var overlaps = new List<string>();
        for (var left = 0; left < metrics.Count; left++)
        {
            for (var right = left + 1; right < metrics.Count; right++)
            {
                var first = metrics[left];
                var second = metrics[right];
                if (!first.Found || !second.Found)
                    continue;

                var intersection = Rect.Intersect(
                    first.ToRect(),
                    second.ToRect());
                if (!intersection.IsEmpty &&
                    intersection.Width > BoundsToleranceDip &&
                    intersection.Height > BoundsToleranceDip)
                {
                    overlaps.Add($"{first.Name}|{second.Name}");
                }
            }
        }

        return overlaps.ToArray();
    }

    private static FrameworkElement FindNamedElement(
        DependencyObject root,
        string name) =>
        EnumerateVisualDescendants<FrameworkElement>(root)
            .Prepend(root as FrameworkElement)
            .FirstOrDefault(element =>
                element is not null &&
                string.Equals(
                    element.Name,
                    name,
                    StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"Named layout element was not found: {name}");

    private static Rect GetBounds(
        FrameworkElement root,
        FrameworkElement element)
    {
        if (ReferenceEquals(root, element))
            return new Rect(new Point(0, 0), element.RenderSize);

        return element
            .TransformToAncestor(root)
            .TransformBounds(
                new Rect(new Point(0, 0), element.RenderSize));
    }

    private static bool IsWithin(Size clientSize, Rect bounds) =>
        bounds.Width > 0d &&
        bounds.Height > 0d &&
        bounds.Left >= -BoundsToleranceDip &&
        bounds.Top >= -BoundsToleranceDip &&
        bounds.Right <= clientSize.Width + BoundsToleranceDip &&
        bounds.Bottom <= clientSize.Height + BoundsToleranceDip;

    private static IEnumerable<T> EnumerateVisualDescendants<T>(
        DependencyObject root)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;

            foreach (var descendant in
                     EnumerateVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void CapturePng(
        FrameworkElement root,
        Size clientSize,
        int dpi,
        string path)
    {
        var scale = dpi / 96d;
        var pixelWidth = Math.Max(
            1,
            (int)Math.Ceiling(clientSize.Width * scale));
        var pixelHeight = Math.Max(
            1,
            (int)Math.Ceiling(clientSize.Height * scale));
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi,
            dpi,
            PixelFormats.Pbgra32);
        bitmap.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static byte[] ReadEmbeddedApplicationManifest(
        string executablePath)
    {
        var module = LoadLibraryEx(
            executablePath,
            IntPtr.Zero,
            LoadLibraryAsDataFile);
        if (module == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Desktop App executable could not be loaded as data: {executablePath}");
        }

        try
        {
            IntPtr resource = IntPtr.Zero;
            foreach (var resourceId in new[] { 1, 2 })
            {
                resource = FindResource(
                    module,
                    new IntPtr(resourceId),
                    new IntPtr(ResourceTypeManifest));
                if (resource != IntPtr.Zero)
                    break;
            }

            if (resource == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Desktop App executable has no embedded application manifest: {executablePath}");
            }

            var size = SizeofResource(module, resource);
            if (size == 0)
            {
                throw new InvalidOperationException(
                    $"Desktop App embedded manifest is empty: {executablePath}");
            }

            var loadedResource = LoadResource(module, resource);
            var resourcePointer = LockResource(loadedResource);
            if (loadedResource == IntPtr.Zero ||
                resourcePointer == IntPtr.Zero)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Desktop App embedded manifest could not be read: {executablePath}");
            }

            var bytes = new byte[checked((int)size)];
            Marshal.Copy(
                resourcePointer,
                bytes,
                0,
                bytes.Length);
            return bytes;
        }
        finally
        {
            _ = FreeLibrary(module);
        }
    }

    private static string DecodeManifest(byte[] bytes)
    {
        if (bytes.Length >= 2 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes);
        }

        if (bytes.Length >= 2 &&
            bytes[0] == 0xFE &&
            bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static SourceProvenance CollectSourceProvenance()
    {
        var desktopAssembly = typeof(MainWindow).Assembly;
        if (string.IsNullOrWhiteSpace(desktopAssembly.Location))
        {
            throw new InvalidOperationException(
                "Loaded Desktop App assembly has no resolvable file location.");
        }

        var desktopAppDllPath = Path.GetFullPath(desktopAssembly.Location);
        if (!File.Exists(desktopAppDllPath))
        {
            throw new FileNotFoundException(
                "Loaded Desktop App assembly file was not found.",
                desktopAppDllPath);
        }

        var repositoryRoot = DiscoverRepositoryRoot(desktopAppDllPath);
        EnsurePathIsInsideRepository(
            repositoryRoot,
            desktopAppDllPath,
            "Loaded Desktop App assembly");
        var desktopAppExePath = Path.ChangeExtension(
            desktopAppDllPath,
            ".exe");
        if (!File.Exists(desktopAppExePath))
        {
            throw new FileNotFoundException(
                "Built Desktop App executable was not found beside the loaded assembly.",
                desktopAppExePath);
        }
        EnsurePathIsInsideRepository(
            repositoryRoot,
            desktopAppExePath,
            "Built Desktop App executable");

        var mainWindowXamlPath = Path.GetFullPath(
            Path.Combine(
                repositoryRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "MainWindow.xaml"));
        if (!File.Exists(mainWindowXamlPath))
        {
            throw new FileNotFoundException(
                "MainWindow.xaml source file was not found.",
                mainWindowXamlPath);
        }

        var desktopAppDirectory =
            Path.GetDirectoryName(mainWindowXamlPath)
            ?? throw new InvalidOperationException(
                "Desktop App source directory could not be resolved.");
        var mainWindowCodeBehindPath = Path.Combine(
            desktopAppDirectory,
            "MainWindow.xaml.cs");
        var responsivePolicyPath = Path.Combine(
            desktopAppDirectory,
            "Infrastructure",
            "MainWindowResponsiveLayoutPolicy.cs");
        var appManifestPath = Path.Combine(
            desktopAppDirectory,
            "app.manifest");
        var desktopProjectPath = Directory
            .GetFiles(desktopAppDirectory, "*.csproj")
            .Single();
        var layoutSourcePaths = new[]
        {
            mainWindowXamlPath,
            mainWindowCodeBehindPath,
            responsivePolicyPath,
            appManifestPath,
            desktopProjectPath
        };
        foreach (var sourcePath in layoutSourcePaths)
        {
            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    "Required responsive layout source was not found.",
                    sourcePath);
            }
        }

        var auditProgramPath = Path.GetFullPath(
            Path.Combine(
                repositoryRoot,
                "tasks",
                "MainWindowDpiRuntimeAudit",
                "Program.cs"));
        var auditProjectPath = Path.GetFullPath(
            Path.Combine(
                repositoryRoot,
                "tasks",
                "MainWindowDpiRuntimeAudit",
                "MainWindowDpiRuntimeAudit.csproj"));
        var auditAssembly = Assembly.GetExecutingAssembly();
        if (string.IsNullOrWhiteSpace(auditAssembly.Location))
        {
            throw new InvalidOperationException(
                "Audit assembly has no resolvable file location.");
        }

        var auditAssemblyPath =
            Path.GetFullPath(auditAssembly.Location);
        var auditExePath =
            Path.ChangeExtension(auditAssemblyPath, ".exe");
        var auditPaths = new[]
        {
            auditProgramPath,
            auditProjectPath,
            auditAssemblyPath,
            auditExePath
        };
        foreach (var auditPath in auditPaths)
        {
            if (!File.Exists(auditPath))
            {
                throw new FileNotFoundException(
                    "Required audit source or binary was not found.",
                    auditPath);
            }

            EnsurePathIsInsideRepository(
                repositoryRoot,
                auditPath,
                "DPI runtime audit source or binary");
        }

        var desktopAppDllWriteTimeUtc =
            File.GetLastWriteTimeUtc(desktopAppDllPath);
        var latestLayoutSourceWriteTimeUtc = layoutSourcePaths
            .Select(File.GetLastWriteTimeUtc)
            .Max();
        if (desktopAppDllWriteTimeUtc <
            latestLayoutSourceWriteTimeUtc)
        {
            throw new InvalidOperationException(
                $"Loaded Desktop App assembly is older than responsive layout source. DLL={desktopAppDllWriteTimeUtc:O}; source={latestLayoutSourceWriteTimeUtc:O}");
        }

        var latestAuditSourceWriteTimeUtc = new[]
        {
            auditProgramPath,
            auditProjectPath
        }
            .Select(File.GetLastWriteTimeUtc)
            .Max();
        var auditAssemblyWriteTimeUtc =
            File.GetLastWriteTimeUtc(auditAssemblyPath);
        if (auditAssemblyWriteTimeUtc <
            latestAuditSourceWriteTimeUtc)
        {
            throw new InvalidOperationException(
                $"Loaded audit assembly is older than audit source. DLL={auditAssemblyWriteTimeUtc:O}; source={latestAuditSourceWriteTimeUtc:O}");
        }

        var embeddedManifestBytes =
            ReadEmbeddedApplicationManifest(desktopAppExePath);
        var embeddedManifestText =
            DecodeManifest(embeddedManifestBytes);
        if (!embeddedManifestText.Contains(
                "PerMonitorV2",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Built Desktop App executable manifest does not declare PerMonitorV2.");
        }

        var resolvedGitRoot = Path.GetFullPath(
            RunGit(repositoryRoot, "rev-parse", "--show-toplevel"));
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(repositoryRoot),
                Path.TrimEndingDirectorySeparator(resolvedGitRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Resolved Git root '{resolvedGitRoot}' does not match repository root '{repositoryRoot}'.");
        }

        var gitHead = RunGit(repositoryRoot, "rev-parse", "HEAD");
        if (gitHead.Length is not (40 or 64) ||
            !gitHead.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                $"Git HEAD was not a valid object ID: '{gitHead}'.");
        }

        var gitStatus = RunGit(
            repositoryRoot,
            "status",
            "--porcelain=v1",
            "--untracked-files=normal");
        var gitDirty = !string.IsNullOrWhiteSpace(gitStatus);

        return new SourceProvenance(
            desktopAppDllPath,
            ComputeSha256(desktopAppDllPath),
            desktopAssembly.ManifestModule.ModuleVersionId.ToString("D"),
            desktopAppDllWriteTimeUtc,
            desktopAppExePath,
            ComputeSha256(desktopAppExePath),
            Convert.ToHexString(
                SHA256.HashData(embeddedManifestBytes)),
            true,
            mainWindowXamlPath,
            ComputeSha256(mainWindowXamlPath),
            ComputeSha256(mainWindowCodeBehindPath),
            ComputeSha256(responsivePolicyPath),
            ComputeSha256(appManifestPath),
            latestLayoutSourceWriteTimeUtc,
            true,
            auditProgramPath,
            ComputeSha256(auditProgramPath),
            auditProjectPath,
            ComputeSha256(auditProjectPath),
            auditAssemblyPath,
            ComputeSha256(auditAssemblyPath),
            auditAssembly.ManifestModule.ModuleVersionId.ToString("D"),
            auditAssemblyWriteTimeUtc,
            auditExePath,
            ComputeSha256(auditExePath),
            latestAuditSourceWriteTimeUtc,
            true,
            repositoryRoot,
            gitHead,
            gitDirty,
            $"{gitHead}-{(gitDirty ? "dirty" : "clean")}");
    }

    private static string DiscoverRepositoryRoot(
        string desktopAppDllPath)
    {
        var startDirectory =
            Path.GetDirectoryName(desktopAppDllPath);
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            throw new InvalidOperationException(
                "Loaded Desktop App assembly directory could not be resolved.");
        }

        for (var candidate = new DirectoryInfo(
                 Path.GetFullPath(startDirectory));
             candidate is not null;
             candidate = candidate.Parent)
        {
            var gitMarker = Path.Combine(
                candidate.FullName,
                ".git");
            var mainWindowSource = Path.Combine(
                candidate.FullName,
                "Desktop",
                "거래플랜.Desktop.App",
                "MainWindow.xaml");
            if ((Directory.Exists(gitMarker) ||
                 File.Exists(gitMarker)) &&
                File.Exists(mainWindowSource))
            {
                return Path.GetFullPath(candidate.FullName);
            }
        }

        throw new InvalidOperationException(
            "Repository root containing .git and Desktop/거래플랜.Desktop.App/MainWindow.xaml could not be resolved.");
    }

    private static void EnsurePathIsInsideRepository(
        string repositoryRoot,
        string candidatePath,
        string label)
    {
        var relativePath = Path.GetRelativePath(
            Path.GetFullPath(repositoryRoot),
            Path.GetFullPath(candidatePath));
        if (Path.IsPathRooted(relativePath) ||
            string.Equals(
                relativePath,
                "..",
                StringComparison.Ordinal) ||
            relativePath.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{label} is outside the resolved repository root: {candidatePath}");
        }
    }

    private static string RunGit(
        string repositoryRoot,
        params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add("-C");
        process.StartInfo.ArgumentList.Add(repositoryRoot);
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
            throw new InvalidOperationException("Failed to start Git.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var output = standardOutput.GetAwaiter().GetResult().Trim();
        var error = standardError.GetAwaiter().GetResult().Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git command failed ({process.ExitCode}): {error}");
        }

        return output;
    }

    private static string ResolveEvidenceDirectory(string[] args)
    {
        var outputIndex = Array.FindIndex(
            args,
            value => string.Equals(
                value,
                "--output",
                StringComparison.OrdinalIgnoreCase));
        if (outputIndex >= 0 && outputIndex + 1 < args.Length)
            return Path.GetFullPath(args[outputIndex + 1]);

        return Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "main-window-dpi-runtime-audit"));
    }

    private static void PrepareEvidenceDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                throw new InvalidOperationException(
                    $"Evidence directory must be new or empty: {path}");
            }

            return;
        }

        Directory.CreateDirectory(path);
    }

    private static IEnumerable<string> BuildMarkdown(
        AuditPayload payload,
        string jsonPath)
    {
        yield return "# MainWindow DPI/저해상도 WPF 런타임 감사";
        yield return "";
        yield return $"- 작성시각: {payload.CreatedAt:O}";
        yield return $"- 결과: **{payload.Result}**";
        yield return $"- 증거 성격: {payload.EvidenceKind}";
        yield return "";
        yield return "## Source provenance";
        yield return "";
        yield return
            $"- Desktop App DLL: `{payload.Provenance.DesktopAppDllPath}`";
        yield return
            $"- Desktop App DLL SHA-256: `{payload.Provenance.DesktopAppDllSha256}`";
        yield return
            $"- Desktop App assembly MVID: `{payload.Provenance.DesktopAppAssemblyMvid}`";
        yield return
            $"- Desktop App DLL last write UTC: `{payload.Provenance.DesktopAppDllWriteTimeUtc:O}`";
        yield return
            $"- Desktop App EXE: `{payload.Provenance.DesktopAppExePath}`";
        yield return
            $"- Desktop App EXE SHA-256: `{payload.Provenance.DesktopAppExeSha256}`";
        yield return
            $"- embedded manifest SHA-256: `{payload.Provenance.EmbeddedManifestSha256}`";
        yield return
            $"- embedded PerMonitorV2: `{payload.Provenance.EmbeddedPerMonitorV2}`";
        yield return
            $"- MainWindow.xaml: `{payload.Provenance.MainWindowXamlPath}`";
        yield return
            $"- MainWindow.xaml SHA-256: `{payload.Provenance.MainWindowXamlSha256}`";
        yield return
            $"- MainWindow.xaml.cs SHA-256: `{payload.Provenance.MainWindowCodeBehindSha256}`";
        yield return
            $"- responsive policy SHA-256: `{payload.Provenance.ResponsivePolicySha256}`";
        yield return
            $"- app.manifest SHA-256: `{payload.Provenance.AppManifestSha256}`";
        yield return
            $"- latest layout source write UTC: `{payload.Provenance.LatestLayoutSourceWriteTimeUtc:O}`";
        yield return
            $"- loaded assembly fresh for layout sources: `{payload.Provenance.LayoutBuildFresh}`";
        yield return
            $"- audit Program.cs: `{payload.Provenance.AuditProgramPath}`";
        yield return
            $"- audit Program.cs SHA-256: `{payload.Provenance.AuditProgramSha256}`";
        yield return
            $"- audit project: `{payload.Provenance.AuditProjectPath}`";
        yield return
            $"- audit project SHA-256: `{payload.Provenance.AuditProjectSha256}`";
        yield return
            $"- audit assembly: `{payload.Provenance.AuditAssemblyPath}`";
        yield return
            $"- audit assembly SHA-256: `{payload.Provenance.AuditAssemblySha256}`";
        yield return
            $"- audit assembly MVID: `{payload.Provenance.AuditAssemblyMvid}`";
        yield return
            $"- audit assembly last write UTC: `{payload.Provenance.AuditAssemblyWriteTimeUtc:O}`";
        yield return
            $"- audit executable: `{payload.Provenance.AuditExePath}`";
        yield return
            $"- audit executable SHA-256: `{payload.Provenance.AuditExeSha256}`";
        yield return
            $"- latest audit source write UTC: `{payload.Provenance.LatestAuditSourceWriteTimeUtc:O}`";
        yield return
            $"- audit assembly fresh for audit sources: `{payload.Provenance.AuditBuildFresh}`";
        yield return
            $"- repository root: `{payload.Provenance.RepositoryRoot}`";
        yield return
            $"- Git HEAD: `{payload.Provenance.GitHead}`";
        yield return
            $"- Git dirty: `{payload.Provenance.GitDirty}`";
        yield return
            $"- source state: `{payload.Provenance.SourceState}`";
        yield return
            $"- 보수적 non-client allowance: width {payload.NonClientWidthAllowanceDip:N0} DIP / height {payload.NonClientHeightAllowanceDip:N0} DIP";
        yield return "";
        yield return "| 프로필 | 작업영역 px | DPI | 창 DIP | client DIP | viewport / extent DIP | 본문 DIP | compact | scroll fallback | update | responsive state | 결과 |";
        yield return "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|";

        foreach (var profile in payload.Profiles)
        {
            yield return
                $"| {profile.Name} | {profile.WorkAreaWidthPixels}×{profile.WorkAreaHeightPixels} | {profile.Dpi} | {profile.WindowWidthDip:N1}×{profile.WindowHeightDip:N1} | {profile.ClientWidthDip:N1}×{profile.ClientHeightDip:N1} | {profile.ViewportWidthDip:N1}×{profile.ViewportHeightDip:N1} / {profile.ContentExtentWidthDip:N1}×{profile.ContentExtentHeightDip:N1} | {profile.WorkspaceWidthDip:N1}×{profile.WorkspaceHeightDip:N1} | {profile.CompactLayoutExpected} | {profile.RootScrollFallbackExpected} / {profile.RootScrollFallbackStateMatches} | {profile.ShowUpdateBanner} | {profile.ResponsiveStateMatches && profile.UpdateSurfaceMatches} | {(profile.Passed ? "PASS" : "FAIL")} |";
        }

        yield return "";
        foreach (var profile in payload.Profiles)
        {
            yield return $"## {profile.Name}";
            yield return "";
            yield return
                $"- navigation overlap: {(profile.NavigationOverlapPairs.Length == 0 ? "없음" : string.Join(", ", profile.NavigationOverlapPairs))}";
            yield return
                $"- compact expected / responsive state / toggle reachable: {profile.CompactLayoutExpected} / {profile.ResponsiveStateMatches} / {profile.CompactToggleWithinRequiredBounds}";
            yield return
                $"- root scroll fallback expected / state: {profile.RootScrollFallbackExpected} / {profile.RootScrollFallbackStateMatches}";
            yield return
                $"- root scroll visibility / scrollable: {profile.RootHorizontalScrollBarVisibility} / {profile.RootVerticalScrollBarVisibility} / {profile.RootScrollableWidthDip:N1}×{profile.RootScrollableHeightDip:N1} DIP";
            yield return
                $"- root viewport / content extent: {profile.ViewportWidthDip:N1}×{profile.ViewportHeightDip:N1} / {profile.ContentExtentWidthDip:N1}×{profile.ContentExtentHeightDip:N1} DIP";
            yield return
                $"- root scroll offset: {profile.HorizontalOffsetDip:N1}, {profile.VerticalOffsetDip:N1} DIP";
            yield return
                $"- scroll max offsets / required actions / origin restore: {profile.MaxScrollOffsetsReached} / {profile.RequiredActionsBroughtIntoViewport} / {profile.ScrollOriginRestored}";
            yield return
                $"- update banner requested / update surface state: {profile.ShowUpdateBanner} / {profile.UpdateSurfaceMatches}";
            yield return
                $"- contract alert panel height: {profile.AlertPanelHeightDip:N1} DIP";
            yield return
                $"- screenshot: `{profile.ScreenshotPath}`";
            yield return
                $"- screenshot SHA-256: `{profile.ScreenshotSha256}`";
            if (!string.IsNullOrWhiteSpace(
                    profile.ScrollEndScreenshotPath))
            {
                yield return
                    $"- scroll-end screenshot: `{profile.ScrollEndScreenshotPath}`";
                yield return
                    $"- scroll-end screenshot SHA-256: `{profile.ScrollEndScreenshotSha256}`";
            }
            yield return "";
        }

        yield return $"JSON: `{jsonPath}`";
    }

    private sealed record LayoutProfile(
        string Name,
        int WorkAreaWidthPixels,
        int WorkAreaHeightPixels,
        int Dpi,
        bool ShowUpdateBanner = false,
        bool AllowRootScrolling = false);

    private sealed record ShutdownPopupAuditResult(
        bool Passed,
        string MarkdownPath);

    private sealed record PopupScreenshotEvidence(
        int ScalePercent,
        int Dpi,
        string Path,
        int PixelWidth,
        int PixelHeight,
        string Sha256);

    private sealed record ButtonMetric(
        string Name,
        bool Found,
        double X,
        double Y,
        double Width,
        double Height,
        bool WithinClientBounds,
        bool WithinContentExtentBounds)
    {
        public Rect ToRect() => new(X, Y, Width, Height);
    }

    private sealed record ScrollReachabilityResult(
        bool MaxOffsetsReached,
        bool RequiredActionsBroughtIntoViewport,
        bool OriginRestored,
        string? ScrollEndScreenshotPath,
        string? ScrollEndScreenshotSha256);

    private sealed record ProfileResult(
        string Name,
        int WorkAreaWidthPixels,
        int WorkAreaHeightPixels,
        int Dpi,
        double Scale,
        double WindowWidthDip,
        double WindowHeightDip,
        double ClientWidthDip,
        double ClientHeightDip,
        double WorkspaceWidthDip,
        double WorkspaceHeightDip,
        double AlertPanelHeightDip,
        bool CompactLayoutExpected,
        bool ResponsiveStateMatches,
        bool CompactToggleWithinRequiredBounds,
        bool ShowUpdateBanner,
        bool UpdateSurfaceMatches,
        bool RootScrollFallbackExpected,
        bool RootScrollFallbackStateMatches,
        string RootHorizontalScrollBarVisibility,
        string RootVerticalScrollBarVisibility,
        double RootScrollableWidthDip,
        double RootScrollableHeightDip,
        double ViewportWidthDip,
        double ViewportHeightDip,
        double ContentExtentWidthDip,
        double ContentExtentHeightDip,
        double HorizontalOffsetDip,
        double VerticalOffsetDip,
        bool MaxScrollOffsetsReached,
        bool RequiredActionsBroughtIntoViewport,
        bool ScrollOriginRestored,
        bool Passed,
        string[] NavigationOverlapPairs,
        ButtonMetric[] Buttons,
        string ScreenshotPath,
        string ScreenshotSha256,
        string? ScrollEndScreenshotPath,
        string? ScrollEndScreenshotSha256);

    private sealed record AuditPayload(
        DateTimeOffset CreatedAt,
        string Result,
        string EvidenceKind,
        SourceProvenance Provenance,
        double NonClientWidthAllowanceDip,
        double NonClientHeightAllowanceDip,
        ProfileResult[] Profiles);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(
        string fileName,
        IntPtr fileHandle,
        uint flags);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "FindResourceW",
        SetLastError = true)]
    private static extern IntPtr FindResource(
        IntPtr module,
        IntPtr name,
        IntPtr type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(
        IntPtr module,
        IntPtr resource);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(
        IntPtr module,
        IntPtr resource);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(
        IntPtr resourceData);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeLibrary(IntPtr module);

    private sealed record SourceProvenance(
        string DesktopAppDllPath,
        string DesktopAppDllSha256,
        string DesktopAppAssemblyMvid,
        DateTime DesktopAppDllWriteTimeUtc,
        string DesktopAppExePath,
        string DesktopAppExeSha256,
        string EmbeddedManifestSha256,
        bool EmbeddedPerMonitorV2,
        string MainWindowXamlPath,
        string MainWindowXamlSha256,
        string MainWindowCodeBehindSha256,
        string ResponsivePolicySha256,
        string AppManifestSha256,
        DateTime LatestLayoutSourceWriteTimeUtc,
        bool LayoutBuildFresh,
        string AuditProgramPath,
        string AuditProgramSha256,
        string AuditProjectPath,
        string AuditProjectSha256,
        string AuditAssemblyPath,
        string AuditAssemblySha256,
        string AuditAssemblyMvid,
        DateTime AuditAssemblyWriteTimeUtc,
        string AuditExePath,
        string AuditExeSha256,
        DateTime LatestAuditSourceWriteTimeUtc,
        bool AuditBuildFresh,
        string RepositoryRoot,
        string GitHead,
        bool GitDirty,
        string SourceState);

    private sealed class AuditMainWindowDataContext
    {
        private readonly bool _showUpdateBanner;

        public AuditMainWindowDataContext(bool showUpdateBanner)
        {
            _showUpdateBanner = showUpdateBanner;
        }

        public bool IsDesktopUpdateBannerVisible => _showUpdateBanner;
        public string DesktopUpdateVersionText =>
            "테스트 업데이트 1.1.999";
        public string DesktopUpdateStatusText =>
            "DPI compact 업데이트 경로 감사";
        public string DesktopUpdateActionText => "업데이트";
        public bool ShowDashboardSalesMetricToggle => true;
        public bool ShowDashboardExpandedSalesCards => true;
        public bool CanViewDashboardSalesCards => true;
        public bool HasDashboardRecycleBinItems => false;
        public bool HasDashboardContractAlerts => false;
        public bool IsPreviewCustomerInfoReadOnly => true;
        public int DashboardSummaryColumnCount => 8;
        public int DashboardCustomersWithContractsCount => 0;
        public int DashboardContractExpiredCount => 0;
        public int DashboardContractExpiringSoonCount => 0;
        public string DashboardContractAlertSummary => "계약서 알림 없음";
        public string CurrentUserDisplay =>
            "테스트관리자 (Admin/USENET) | 업체DB: USENET_GROUP (오프라인)";
        public string SyncStatus => "격리 WPF 레이아웃 감사";
        public IReadOnlyList<object> DashboardContractAlerts =>
            Array.Empty<object>();
        public IReadOnlyList<object> VoucherTypeFilterOptions =>
            Array.Empty<object>();
        public IReadOnlyList<object> InvoiceOfficeFilterOptions =>
            Array.Empty<object>();
        public IReadOnlyList<object> FavoriteInvoices =>
            Array.Empty<object>();
        public IReadOnlyList<object> FilteredCustomers =>
            Array.Empty<object>();
        public IReadOnlyList<object> InvoiceRows =>
            Array.Empty<object>();
        public IReadOnlyList<object> PreviewLines =>
            Array.Empty<object>();
    }
}
