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
        Assert.Contains("SaveDocumentAsXps", executor, StringComparison.Ordinal);
        Assert.Contains("SaveDocumentAsPdf", executor, StringComparison.Ordinal);
        Assert.Contains("XpsDocument.CreateXpsDocumentWriter", executor, StringComparison.Ordinal);
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
