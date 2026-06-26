using System.Reflection;
using System.Windows;
using System.Windows.Documents;
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
        Assert.Contains("SaveDocumentAsXps", executor, StringComparison.Ordinal);
        Assert.Contains("XpsDocument.CreateXpsDocumentWriter", executor, StringComparison.Ordinal);
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
