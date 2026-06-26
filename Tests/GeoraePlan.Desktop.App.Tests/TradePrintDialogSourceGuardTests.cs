using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Markup;
using System.Windows.Media;
using 거래플랜.Desktop.App.Services;
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
        Assert.Contains("StatusTextBlock.Text = \"프린터 속성 창을 열었습니다.", codeBehind, StringComparison.Ordinal);
        Assert.Contains("StatusTextBlock.Text = \"프린터 속성 창을 열 수 없습니다.", codeBehind, StringComparison.Ordinal);
        Assert.Contains("MessageBox.Show(", codeBehind, StringComparison.Ordinal);
        Assert.Contains("$\"프린터 속성 창을 열 수 없습니다.", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("new PrintDialog", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Windows.Controls.PrintDialog", codeBehind, StringComparison.Ordinal);
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
        Assert.Contains("_printerRefreshProvider", codeBehind, StringComparison.Ordinal);
        Assert.Contains("OnRefreshPrintersClick", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GetSelectedQueueName()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("프린터 목록을 새로고침했습니다", codeBehind, StringComparison.Ordinal);
        Assert.Contains("LoadPrinterSnapshotSafely", executor, StringComparison.Ordinal);
        Assert.Contains("LoadPrinterSnapshotSafely,", executor, StringComparison.Ordinal);
        Assert.Contains("currentPageNumber", executor, StringComparison.Ordinal);
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

        Assert.Contains("x:Name=\"CurrentPageRadioButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"현재 페이지\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ConfigureCurrentPageOption", codeBehind, StringComparison.Ordinal);
        Assert.Contains("pageNumbers = [_currentPageNumber.Value]", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CurrentPageNumber", codeBehind, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PreviewDocumentViewer\"", previewXaml, StringComparison.Ordinal);
        Assert.Contains("PreviewDocumentViewer?.MasterPageNumber", previewCodeBehind, StringComparison.Ordinal);
        Assert.Contains("CurrentPageNumberProvider", previewViewModel, StringComparison.Ordinal);
        Assert.Contains("currentPageNumber", printService, StringComparison.Ordinal);
    }

    [Fact]
    public void TradePrintExecutor_LoadsDirectAndDeployedPrinterQueuesForCopierConnections()
    {
        var repoRoot = FindRepositoryRoot();
        var executor = File.ReadAllText(Path.Combine(
            repoRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "TradePrintExecutor.cs"));

        Assert.Contains("InstalledPrinterQueueTypeGroups", executor, StringComparison.Ordinal);
        Assert.Contains("EnumeratedPrintQueueTypes.DirectPrinting", executor, StringComparison.Ordinal);
        Assert.Contains("EnumeratedPrintQueueTypes.PushedMachineConnection", executor, StringComparison.Ordinal);
        Assert.Contains("EnumeratedPrintQueueTypes.PushedUserConnection", executor, StringComparison.Ordinal);
        Assert.Contains("EnumeratedPrintQueueTypes.WorkOffline", executor, StringComparison.Ordinal);
        Assert.Contains("프린터 목록 확인 실패({typeNames})", executor, StringComparison.Ordinal);
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
