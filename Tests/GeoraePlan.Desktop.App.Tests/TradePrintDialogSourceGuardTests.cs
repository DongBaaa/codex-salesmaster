using System.Reflection;
using System.Printing;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.Views;
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
        Assert.Contains("PDF 저장", xaml, StringComparison.Ordinal);
        Assert.Contains("복합기가 잡히지 않으면 PDF 저장 후 복합기/다른 PC에서 출력하세요.", xaml, StringComparison.Ordinal);
        Assert.Contains("SaveDocumentAsXps", executor, StringComparison.Ordinal);
        Assert.Contains("SaveDocumentAsPdf", executor, StringComparison.Ordinal);
        Assert.Contains("XpsDocument.CreateXpsDocumentWriter", executor, StringComparison.Ordinal);
    }

    [Fact]
    public void TradePrintWindow_KeepsPrintActionFooterVisibleWhenOptionsOverflow()
    {
        var repoRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml"));

        var scrollViewerIndex = xaml.IndexOf("<ScrollViewer Grid.Row=\"0\"", StringComparison.Ordinal);
        var scrollViewerEndIndex = xaml.IndexOf("</ScrollViewer>", StringComparison.Ordinal);
        var footerIndex = xaml.IndexOf("<Border Grid.Row=\"1\"", scrollViewerEndIndex, StringComparison.Ordinal);
        var printButtonIndex = xaml.IndexOf("x:Name=\"PrintButton\"", StringComparison.Ordinal);

        Assert.True(scrollViewerIndex >= 0, "인쇄 옵션 본문은 스크롤 영역 안에 있어야 합니다.");
        Assert.True(scrollViewerEndIndex > scrollViewerIndex, "스크롤 영역 닫힘 태그를 찾을 수 없습니다.");
        Assert.True(footerIndex > scrollViewerEndIndex, "PDF 저장/파일 저장/인쇄/취소 버튼 푸터는 스크롤 영역 밖 하단 고정 행에 있어야 합니다.");
        Assert.True(printButtonIndex > footerIndex, "인쇄 버튼은 하단 고정 푸터 안에 있어야 합니다.");
        Assert.Contains("ResizeMode=\"CanResizeWithGrip\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"인쇄\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"확인\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TradePrintWindow_DisablesDirectPrintWhenPrinterIsUnavailableAndGuidesFileFallback()
    {
        var repoRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml.cs"));
        var executor = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "TradePrintExecutor.cs"));

        Assert.Contains("x:Name=\"PrintButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PrintButton.IsEnabled = hasPrinter", codeBehind, StringComparison.Ordinal);
        Assert.Contains("등록된 프린터를 찾지 못했습니다", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PDF 저장 또는 파일 저장(XPS)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("프린터가 없거나 복합기 연결이 안 되면 PDF 저장 또는 파일 저장(XPS)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("파일 저장 전용으로 인쇄창을 표시합니다", executor, StringComparison.Ordinal);
        Assert.Contains("프린터가 없거나 복합기 연결이 안 되면 PDF 저장 또는 파일 저장(XPS)", executor, StringComparison.Ordinal);
    }

    [Fact]
    public void TradePrintWindow_UsesDedicatedPrinterPropertyActionWithoutClosingFallbackPrintWindow()
    {
        var repoRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml.cs"));

        Assert.Contains("x:Name=\"PropertiesButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnPrinterPropertiesClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FileName = \"rundll32.exe\"", codeBehind, StringComparison.Ordinal);
        Assert.Contains("printui.dll,PrintUIEntry /p /n", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SetStatus(\"프린터 속성 창을 열었습니다.", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SetStatus(\"프린터 속성 창을 열 수 없습니다.", codeBehind, StringComparison.Ordinal);
        Assert.Contains("MessageBox.Show(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("$\"프린터 속성 창을 열 수 없습니다.", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("new PrintDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows.Controls.PrintDialog", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopSource_DoesNotBypassDedicatedTradePrintWindowWithNativeWpfPrintDialog()
    {
        var repoRoot = FindRepositoryRoot();
        var appRoot = Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App");

        var sourceFiles = Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.NotEmpty(sourceFiles);
        foreach (var sourceFile in sourceFiles)
        {
            var source = File.ReadAllText(sourceFile);
            Assert.DoesNotContain("new PrintDialog", source, StringComparison.Ordinal);
            Assert.DoesNotContain("System.Windows.Controls.PrintDialog", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TradePrintWindow_CanOpenWindowsPrinterManagementFromDedicatedDialog()
    {
        var repoRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml.cs"));

        Assert.Contains("x:Name=\"OpenPrinterManagementButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnOpenPrinterManagementClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("프린터 관리", xaml, StringComparison.Ordinal);
        Assert.Contains("OnOpenPrinterManagementClick", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TryOpenPrinterManagement", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ms-settings:printers", codeBehind, StringComparison.Ordinal);
        Assert.Contains("control.exe", codeBehind, StringComparison.Ordinal);
        Assert.Contains("printers", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void TradePrintWindow_CanRefreshPrinterListWithoutClosingDialog()
    {
        var repoRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml.cs"));
        var executor = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "TradePrintExecutor.cs"));

        Assert.Contains("x:Name=\"RefreshPrintersButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnRefreshPrintersClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("_printerCatalogProvider", codeBehind, StringComparison.Ordinal);
        Assert.Contains("OnRefreshPrintersClick", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GetSelectedQueueName()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("프린터 목록을 새로고침했습니다", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TradePrinterCatalog.LoadSnapshot()", executor, StringComparison.Ordinal);
        Assert.Contains("LoadPrinterSnapshotSafely,", executor, StringComparison.Ordinal);
        Assert.Contains("currentPageNumber", executor, StringComparison.Ordinal);
    }

    [Fact]
    public void TradePrintWindow_ProvidesDiagnosticCopyAndExplicitOnePageTestPrintStates()
    {
        var repoRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml.cs"));
        var executor = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "TradePrintExecutor.cs"));

        Assert.Contains("x:Name=\"CopyDiagnosticButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"진단 복사\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PrintDiagnosticButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"1쪽 테스트\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnCopyDiagnosticClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnPrintDiagnosticClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("OnCopyDiagnosticClick", codeBehind, StringComparison.Ordinal);
        Assert.Contains("OnPrintDiagnosticClick", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BuildPrinterDiagnosticReport", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SetClipboardTextWithRetry", codeBehind, StringComparison.Ordinal);
        Assert.Contains("Clipboard.SetDataObject(text, copy: true)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("const int maxAttempts = 5", codeBehind, StringComparison.Ordinal);
        Assert.Contains("const int retryDelayMilliseconds = 80", codeBehind, StringComparison.Ordinal);
        Assert.Contains("프린터 진단 정보를 클립보드에 복사하지 못했습니다", codeBehind, StringComparison.Ordinal);
        Assert.Contains("오프라인이라 1쪽 테스트 인쇄를 보내지 않았습니다", codeBehind, StringComparison.Ordinal);
        Assert.Contains("1쪽 테스트 인쇄를 보내지 못했습니다", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PDF/XPS fallback", codeBehind, StringComparison.Ordinal);
        Assert.Contains("TryPrintDiagnosticPage", executor, StringComparison.Ordinal);
        Assert.Contains("거래플랜 프린터 진단 페이지", executor, StringComparison.Ordinal);
        Assert.Contains("1쪽 테스트 인쇄 출력 오류", executor, StringComparison.Ordinal);
    }

    [Fact]
    public void TradePrintWindow_OffersCurrentPreviewPageOption()
    {
        var repoRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml.cs"));
        var previewXaml = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "PrintPreviewWindow.xaml"));
        var previewCodeBehind = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "PrintPreviewWindow.xaml.cs"));
        var previewViewModel = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "PrintPreviewViewModel.cs"));
        var printService = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "WpfInvoicePrintService.cs"));
        var flowPreviewHelper = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "PrintPreviewHelper.cs"));

        Assert.Contains("x:Name=\"CurrentPageRadioButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"현재 페이지\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ConfigureCurrentPageOption", codeBehind, StringComparison.Ordinal);
        Assert.Contains("pageNumbers = [_currentPageNumber.Value]", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CurrentPageNumber", codeBehind, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PreviewDocumentViewer\"", previewXaml, StringComparison.Ordinal);
        Assert.Contains("PreviewDocumentViewer?.MasterPageNumber", previewCodeBehind, StringComparison.Ordinal);
        Assert.Contains("CurrentPageNumberProvider", previewViewModel, StringComparison.Ordinal);
        Assert.Contains("currentPageNumber", printService, StringComparison.Ordinal);
        Assert.Contains("new DocumentViewer", flowPreviewHelper, StringComparison.Ordinal);
        Assert.Contains("viewer?.MasterPageNumber", flowPreviewHelper, StringComparison.Ordinal);
        Assert.Contains("NormalizeCurrentPageNumber", flowPreviewHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("new FlowDocumentScrollViewer", flowPreviewHelper, StringComparison.Ordinal);
    }

    [Fact]
    public void TradePrintCatalog_LoadsAllNativeLocalAndConnectedPrintersWithoutPrintQueueObjects()
    {
        var repoRoot = FindRepositoryRoot();
        var catalog = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Printing",
            "TradePrinterCatalog.cs"));

        Assert.Contains("EntryPoint = \"EnumPrintersW\"", catalog, StringComparison.Ordinal);
        Assert.Contains("EntryPoint = \"GetDefaultPrinterW\"", catalog, StringComparison.Ordinal);
        Assert.Contains("PrinterEnumLocal | PrinterEnumConnections", catalog, StringComparison.Ordinal);
        Assert.Contains("PrinterInfoLevel = 2", catalog, StringComparison.Ordinal);
        Assert.Contains("ErrorInsufficientBuffer = 122", catalog, StringComparison.Ordinal);
        Assert.Contains("MaxEnumerationAttempts = 3", catalog, StringComparison.Ordinal);
        Assert.Contains("attempt <= MaxEnumerationAttempts", catalog, StringComparison.Ordinal);
        Assert.Contains("requiredBytesAfterRead", catalog, StringComparison.Ordinal);
        Assert.Contains("bufferSize = Math.Max(bufferSize, requiredBytesAfterRead);", catalog, StringComparison.Ordinal);
        Assert.Contains("ReadPrinterInfo(buffer, returnedCount)", catalog, StringComparison.Ordinal);
        Assert.Contains("PrinterInfoSnapshot", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Printing", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("PrintQueue", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void TradePrintCatalog_NativeSnapshotContainsEveryInstalledNameAndRichStatus()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var installedNames = TradePrinterCatalog.LoadWindowsInstalledPrinterNames();
        var snapshot = TradePrinterCatalog.LoadSnapshot();
        var visibleNames = snapshot.Printers
            .Select(static printer => printer.QueueName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(
            installedNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase),
            visibleNames.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            installedNames.Count,
            installedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(snapshot.Printers, printer =>
        {
            Assert.False(string.IsNullOrWhiteSpace(printer.QueueName));
            Assert.False(string.IsNullOrWhiteSpace(printer.DisplayName));
            Assert.False(string.IsNullOrWhiteSpace(printer.TypeText));
            Assert.False(string.IsNullOrWhiteSpace(printer.LocationText));
            Assert.False(string.IsNullOrWhiteSpace(printer.StatusText));
        });
        Console.WriteLine($"NativeInstalledPrinterCount={installedNames.Count}");
        Console.WriteLine($"VisiblePrinterCatalogCount={snapshot.Printers.Count}");
    }

    [Fact]
    public void TradePrintWindow_PrinterSelectorUsesFullTextNonVirtualizedItems()
    {
        var repoRoot = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "TradePrintWindow.xaml"));

        Assert.DoesNotContain("DisplayMemberPath=\"DisplayName\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizingStackPanel.IsVirtualizing=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ScrollViewer.CanContentScroll=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding DisplayName, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding TypeText, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding LocationText, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding StatusText, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"None\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TradePrintWindow_RealNativeSnapshotRealizesEveryFullPrinterItem()
    {
        if (!OperatingSystem.IsWindows())
            return;

        RunOnSta(() =>
        {
            var snapshot = TradePrinterCatalog.LoadSnapshot();
            Assert.NotEmpty(snapshot.Printers);
            var window = new TradePrintWindow(snapshot, pageCount: 1);
            try
            {
                window.Show();

                var combo = Assert.IsType<ComboBox>(window.FindName("PrinterComboBox"));
                combo.IsDropDownOpen = true;
                combo.Dispatcher.Invoke(
                    static () => { },
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                combo.UpdateLayout();

                Assert.Equal(snapshot.Printers.Count, combo.Items.Count);
                for (var index = 0; index < combo.Items.Count; index++)
                {
                    var item = Assert.IsType<ComboBoxItem>(
                        combo.ItemContainerGenerator.ContainerFromIndex(index));
                    item.ApplyTemplate();
                    item.UpdateLayout();

                    var textBlocks = FindVisualDescendants<TextBlock>(item).ToArray();
                    Assert.NotEmpty(textBlocks);
                    Assert.All(textBlocks, textBlock =>
                    {
                        Assert.Equal(TextWrapping.Wrap, textBlock.TextWrapping);
                        Assert.Equal(TextTrimming.None, textBlock.TextTrimming);
                    });

                    var displayName = combo.Items[index]
                        .GetType()
                        .GetProperty("DisplayName", BindingFlags.Public | BindingFlags.Instance)
                        ?.GetValue(combo.Items[index]) as string;
                    Assert.False(string.IsNullOrWhiteSpace(displayName));
                    Assert.Contains(
                        textBlocks.Select(ReadTextBlockText),
                        text => string.Equals(text, displayName, StringComparison.Ordinal));
                }
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void PrintEnvironmentDiagnosticScript_CapturesPrinterAndFallbackEvidence()
    {
        var repoRoot = FindRepositoryRoot();
        var scriptPath = Path.Combine(
            repoRoot,
            "tools",
            "verification",
            "Test-GeoraePlanPrintEnvironment.ps1");
        var scriptBytes = File.ReadAllBytes(scriptPath);
        var script = File.ReadAllText(scriptPath);

        Assert.True(scriptBytes.Length > 3);
        Assert.Equal(0xEF, scriptBytes[0]);
        Assert.Equal(0xBB, scriptBytes[1]);
        Assert.Equal(0xBF, scriptBytes[2]);
        Assert.Contains("[switch]$RequirePrinter", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$RequireOnlinePrinter", script, StringComparison.Ordinal);
        Assert.Contains("[switch]$FailOnWarnings", script, StringComparison.Ordinal);
        Assert.Contains("System.Printing.LocalPrintServer", script, StringComparison.Ordinal);
        Assert.Contains("DefaultPrintQueue", script, StringComparison.Ordinal);
        Assert.Contains("[System.Printing.EnumeratedPrintQueueTypes]::DirectPrinting", script, StringComparison.Ordinal);
        Assert.Contains("PushedMachineConnection", script, StringComparison.Ordinal);
        Assert.Contains("PushedUserConnection", script, StringComparison.Ordinal);
        Assert.Contains("WorkOffline", script, StringComparison.Ordinal);
        Assert.Contains("System.Drawing.Printing.PrinterSettings", script, StringComparison.Ordinal);
        Assert.Contains("GetPrintQueue([string]$printerName)", script, StringComparison.Ordinal);
        Assert.Contains("TradePrinterCatalog.cs", script, StringComparison.Ordinal);
        Assert.Contains("PrinterEnumLocal | PrinterEnumConnections", script, StringComparison.Ordinal);
        Assert.Contains("EnumPrinters(", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Needle = 'LoadInstalledPrintQueues'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Needle = 'EnumeratedPrintQueueTypes.DirectPrinting'", script, StringComparison.Ordinal);
        Assert.Contains("거래플랜 전용 인쇄", script, StringComparison.Ordinal);
        Assert.Contains("PDF 저장", script, StringComparison.Ordinal);
        Assert.Contains("파일 저장(XPS)", script, StringComparison.Ordinal);
        Assert.Contains("SaveDocumentAsPdf", script, StringComparison.Ordinal);
        Assert.Contains("SaveDocumentAsXps", script, StringComparison.Ordinal);
        Assert.Contains("기본 WPF PrintDialog 직접 호출이 감지되었습니다", script, StringComparison.Ordinal);
        Assert.Contains("Print environment report:", script, StringComparison.Ordinal);
        Assert.Contains("PrinterCount:", script, StringComparison.Ordinal);
        Assert.Contains("OnlinePrinterCount:", script, StringComparison.Ordinal);
        Assert.Contains("## 참고", script, StringComparison.Ordinal);
        Assert.Contains("실제 종이 출력은 현장 장치 상태에 따라 별도 확인이 필요합니다", script, StringComparison.Ordinal);
        Assert.Contains("등록된 Windows 프린터가 없습니다", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TradePrintWindow_DefaultFileNameUsesCustomerAndOutputDocumentNames()
    {
        var repoRoot = FindRepositoryRoot();
        var appRoot = Directory.EnumerateDirectories(
                Path.Combine(repoRoot, "Desktop"),
                "*.Desktop.App",
                SearchOption.TopDirectoryOnly)
            .Single();
        var codeBehind = File.ReadAllText(Path.Combine(
            appRoot,
            "Views",
            "TradePrintWindow.xaml.cs"));
        var executor = File.ReadAllText(Path.Combine(
            appRoot,
            "Services",
            "TradePrintExecutor.cs"));
        var salesViewModel = File.ReadAllText(Path.Combine(
            appRoot,
            "ViewModels",
            "SalesViewModel.cs"));

        Assert.Contains("_defaultFileBaseName", codeBehind, StringComparison.Ordinal);
        Assert.Contains("defaultFileBaseName: jobName", executor, StringComparison.Ordinal);
        Assert.Contains("MakeSafeFileName($\"{_defaultFileBaseName}-", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("\uAC70\uB798\uD50C\uB79C-\uC778\uC1C4\uBB38\uC11C", codeBehind, StringComparison.Ordinal);
        Assert.Contains("BuildPrintOutputJobName(", salesViewModel, StringComparison.Ordinal);
        Assert.Contains("selectedCodes.Select(AttachmentDocumentCatalog.GetDisplayName)", salesViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("\uCD9C\uB825\uBB3C_{invoice.InvoiceDate", salesViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("\uAD6C\uB9E4\uBA85\uC138\uC11C_{invoice.InvoiceDate", salesViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void TradePrintWindow_KeepsFullWidthPrinterSelectorAndActionsVisibleAt780MinWidth()
    {
        RunOnSta(() =>
        {
            var window = new TradePrintWindow(
                new PrinterCatalogSnapshot(Array.Empty<PrinterCatalogItem>(), null),
                pageCount: 1);
            try
            {
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                root.Measure(new Size(window.MinWidth, window.MinHeight));
                root.Arrange(new Rect(0, 0, window.MinWidth, window.MinHeight));
                root.UpdateLayout();

                var printerCombo = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("PrinterComboBox"));
                var printerActionGrid = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("PrinterActionGrid"));
                var propertiesButton = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("PropertiesButton"));
                var copyDiagnosticButton = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("CopyDiagnosticButton"));
                var printDiagnosticButton = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("PrintDiagnosticButton"));

                var comboOrigin = printerCombo.TranslatePoint(new Point(0, 0), root);
                var actionOrigin = printerActionGrid.TranslatePoint(new Point(0, 0), root);
                var propertiesLeft = propertiesButton.TranslatePoint(new Point(0, 0), root).X;
                var copyOrigin = copyDiagnosticButton.TranslatePoint(new Point(0, 0), root);
                var printRight = printDiagnosticButton.TranslatePoint(new Point(printDiagnosticButton.ActualWidth, 0), root).X;

                Assert.True(actionOrigin.Y >= comboOrigin.Y + printerCombo.ActualHeight + 7, "프린터 작업 버튼은 전체 폭 선택 상자 아래에 있어야 합니다.");
                Assert.True(Math.Abs(actionOrigin.Y - copyOrigin.Y) < 1, "모든 프린터 작업 버튼은 같은 행에 있어야 합니다.");
                Assert.True(printerCombo.ActualWidth >= 600, $"780px 최소폭에서도 긴 프린터 이름을 표시할 전체 폭을 확보해야 합니다. ActualWidth={printerCombo.ActualWidth}");
                Assert.True(Math.Abs(printerActionGrid.ActualWidth - printerCombo.ActualWidth) < 1, "프린터 선택 상자와 작업 버튼 행은 같은 전체 폭을 사용해야 합니다.");
                Assert.True(propertiesLeft >= comboOrigin.X - 1, $"첫 프린터 작업 버튼이 왼쪽에서 잘리면 안 됩니다. Left={propertiesLeft}, ComboLeft={comboOrigin.X}");
                Assert.True(printRight <= root.ActualWidth + 1, $"780px 최소폭에서 1쪽 테스트 버튼이 잘리면 안 됩니다. Right={printRight}, RootWidth={root.ActualWidth}");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TradePrintExecutor_SavesPdfFileFromFixedDocument()
    {
        RunOnSta(() =>
        {
            var document = BuildSimpleFixedDocument();
            var outputPath = Path.Combine(Path.GetTempPath(), $"georaeplan-print-test-{Guid.NewGuid():N}.pdf");

            try
            {
                InvokeSaveDocumentAsPdf(document.DocumentPaginator, outputPath);

                Assert.True(File.Exists(outputPath));
                var bytes = File.ReadAllBytes(outputPath);
                Assert.True(bytes.Length > 1000);
                Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(bytes, 0, 5));
            }
            finally
            {
                if (File.Exists(outputPath))
                    File.Delete(outputPath);
            }
        });
    }

    [Fact]
    public void TradePrintExecutor_BuildsSinglePageDiagnosticDocument()
    {
        RunOnSta(() =>
        {
            var document = InvokeBuildDiagnosticDocument(
                "테스트 복합기",
                null,
                new DateTimeOffset(2026, 7, 11, 9, 30, 0, TimeSpan.FromHours(9)));

            document.DocumentPaginator.ComputePageCount();
            Assert.Equal(1, document.DocumentPaginator.PageCount);

            var text = NormalizeText(ReadFixedDocumentText(document));
            Assert.Contains("거래플랜 프린터 진단 페이지", text);
            Assert.Contains("테스트 복합기", text);
            Assert.Contains("이 페이지가 정상적으로 출력되면", text);
            Assert.Contains("fallback: PDF 저장 / 파일 저장(XPS)", text);
        });
    }

    [Fact]
    public void TradePrintExecutor_ReturnsSpecificMessageWhenDiagnosticPrinterIsMissing()
    {
        var result = TradePrintExecutor.TryPrintDiagnosticPage(null, out var errorMessage);

        Assert.False(result);
        Assert.Contains("1쪽 테스트 인쇄를 보낼 프린터를 선택하세요.", errorMessage);
    }

    [Fact]
    public void TradePrintExecutor_UsesInjectedWriterForDiagnosticTestPrintWithoutSendingPaper()
    {
        RunOnSta(() =>
        {
            var queue = (PrintQueue)RuntimeHelpers.GetUninitializedObject(typeof(PrintQueue));
            GC.SuppressFinalize(queue);
            FixedDocument? capturedDocument = null;
            PrintTicket? capturedTicket = null;

            var result = InvokeTryPrintDiagnosticPage(
                queue,
                new DateTimeOffset(2026, 7, 11, 9, 45, 0, TimeSpan.FromHours(9)),
                (_, document, printTicket) =>
                {
                    capturedDocument = document;
                    capturedTicket = printTicket;
                },
                out var errorMessage);

            Assert.True(result, errorMessage);
            Assert.NotNull(capturedDocument);
            Assert.NotNull(capturedTicket);

            capturedDocument!.DocumentPaginator.ComputePageCount();
            Assert.Equal(1, capturedDocument.DocumentPaginator.PageCount);
            Assert.Equal(1, capturedTicket!.CopyCount);
            Assert.Equal(Collation.Collated, capturedTicket.Collation);
        });
    }

    [Fact]
    public void TradePrintExecutor_ExpandsCollatedCopiesBeforeSendingToDriver()
    {
        var source = new RecordingDocumentPaginator(3);
        var paginator = InvokeBuildCopyPaginator(source, copyCount: 2, collate: true);

        Assert.Equal(6, paginator.PageCount);
        source.RequestedPages.Clear();
        for (var index = 0; index < paginator.PageCount; index++)
            paginator.GetPage(index);

        Assert.Equal([0, 1, 2, 0, 1, 2], source.RequestedPages);
    }

    [Fact]
    public void TradePrintExecutor_ExpandsUncollatedCopiesBeforeSendingToDriver()
    {
        var source = new RecordingDocumentPaginator(3);
        var paginator = InvokeBuildCopyPaginator(source, copyCount: 2, collate: false);

        Assert.Equal(6, paginator.PageCount);
        source.RequestedPages.Clear();
        for (var index = 0; index < paginator.PageCount; index++)
            paginator.GetPage(index);

        Assert.Equal([0, 0, 1, 1, 2, 2], source.RequestedPages);
    }

    [Fact]
    public void TradePrintExecutor_AppliesPageSelectionAndReverseOrder()
    {
        var source = new RecordingDocumentPaginator(5);
        var paginator = InvokeBuildTargetPaginator(
            source,
            pageNumbers: [1, 3, 4],
            reversePageOrder: true,
            pageCount: 5);

        Assert.Equal(3, paginator.PageCount);

        for (var index = 0; index < paginator.PageCount; index++)
            paginator.GetPage(index);

        Assert.Equal([3, 2, 0], source.RequestedPages);
    }

    private static DocumentPaginator InvokeBuildCopyPaginator(
        DocumentPaginator source,
        int copyCount,
        bool collate)
    {
        var method = typeof(TradePrintExecutor).GetMethod(
            "BuildCopyPaginator",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [source, copyCount, collate]);
        return Assert.IsAssignableFrom<DocumentPaginator>(result);
    }

    private static DocumentPaginator InvokeBuildTargetPaginator(
        DocumentPaginator source,
        IReadOnlyList<int>? pageNumbers,
        bool reversePageOrder,
        int pageCount)
    {
        var method = typeof(TradePrintExecutor).GetMethod(
            "BuildTargetPaginator",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [source, pageNumbers, reversePageOrder, pageCount]);
        return Assert.IsAssignableFrom<DocumentPaginator>(result);
    }

    private static void InvokeSaveDocumentAsPdf(DocumentPaginator paginator, string outputPath)
    {
        var method = typeof(TradePrintExecutor).GetMethod(
            "SaveDocumentAsPdf",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        method!.Invoke(null, [paginator, outputPath]);
    }

    private static FixedDocument InvokeBuildDiagnosticDocument(
        string printerName,
        PrintQueue? printQueue,
        DateTimeOffset generatedAt)
    {
        var method = typeof(TradePrintExecutor).GetMethod(
            "BuildDiagnosticDocument",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [printerName, printQueue, generatedAt]);
        return Assert.IsAssignableFrom<FixedDocument>(result);
    }

    private static bool InvokeTryPrintDiagnosticPage(
        PrintQueue printQueue,
        DateTimeOffset generatedAt,
        Action<PrintQueue, FixedDocument, PrintTicket> sendDocument,
        out string? errorMessage)
    {
        var method = typeof(TradePrintExecutor)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method =>
            {
                if (!string.Equals(method.Name, "TryPrintDiagnosticPage", StringComparison.Ordinal))
                    return false;

                var parameters = method.GetParameters();
                return parameters.Length == 4 &&
                       parameters[0].ParameterType == typeof(PrintQueue) &&
                       parameters[1].ParameterType == typeof(DateTimeOffset);
            });

        object?[] arguments = [printQueue, generatedAt, sendDocument, null];
        var result = method.Invoke(null, arguments);
        errorMessage = arguments[3] as string;
        return Assert.IsType<bool>(result);
    }

    private static FixedDocument BuildSimpleFixedDocument()
    {
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new Size(300, 420);

        var page = new FixedPage
        {
            Width = 300,
            Height = 420,
            Background = Brushes.White
        };
        page.Children.Add(new TextBlock
        {
            Text = "거래플랜 PDF 저장 테스트",
            FontSize = 20,
            Margin = new Thickness(24)
        });

        var content = new PageContent();
        ((IAddChild)content).AddChild(page);
        document.Pages.Add(content);
        return document;
    }

    private static string ReadFixedDocumentText(FixedDocument document)
    {
        var builder = new StringBuilder();
        foreach (PageContent pageContent in document.Pages)
        {
            if (pageContent.Child is null)
                continue;

            AppendText(pageContent.Child, builder);
        }

        return builder.ToString();
    }

    private static void AppendText(DependencyObject node, StringBuilder builder)
    {
        if (node is TextBlock textBlock)
        {
            var text = ReadTextBlockText(textBlock);
            if (!string.IsNullOrWhiteSpace(text))
                builder.AppendLine(text);
        }
        else if (node is TextElement textElement)
        {
            var text = new TextRange(textElement.ContentStart, textElement.ContentEnd).Text;
            if (!string.IsNullOrWhiteSpace(text))
                builder.AppendLine(text);
        }
        else if (node is ContentControl { Content: string content } && !string.IsNullOrWhiteSpace(content))
        {
            builder.AppendLine(content);
        }

        var childCount = VisualTreeHelper.GetChildrenCount(node);
        for (var i = 0; i < childCount; i++)
            AppendText(VisualTreeHelper.GetChild(node, i), builder);
    }

    private static string ReadTextBlockText(TextBlock textBlock)
    {
        if (!string.IsNullOrWhiteSpace(textBlock.Text))
            return textBlock.Text;

        var builder = new StringBuilder();
        foreach (var inline in textBlock.Inlines)
        {
            if (inline is Run run)
                builder.Append(run.Text);
        }

        return builder.ToString();
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
                yield return match;

            foreach (var descendant in FindVisualDescendants<T>(child))
                yield return descendant;
        }
    }

    private static string NormalizeText(string text)
        => string.Join(" ", text
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Split([' '], StringSplitOptions.RemoveEmptyEntries));

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
            throw captured;
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

    private sealed class RecordingDocumentPaginator : DocumentPaginator
    {
        private readonly int _pageCount;

        public RecordingDocumentPaginator(int pageCount)
        {
            _pageCount = pageCount;
        }

        public List<int> RequestedPages { get; } = [];

        public override bool IsPageCountValid => true;

        public override int PageCount => _pageCount;

        public override Size PageSize { get; set; } = new(100, 100);

        public override IDocumentPaginatorSource? Source => null;

        public override DocumentPage GetPage(int pageNumber)
        {
            RequestedPages.Add(pageNumber);
            return DocumentPage.Missing;
        }
    }
}
