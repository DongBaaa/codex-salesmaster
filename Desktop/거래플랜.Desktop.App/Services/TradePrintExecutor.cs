using System.IO;
using System.IO.Packaging;
using System.Printing;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using System.Windows.Xps.Packaging;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Views;
using WpfSize = System.Windows.Size;

namespace 거래플랜.Desktop.App.Services;

public static class TradePrintExecutor
{
    public const double A4Width = 793.7;
    public const double A4Height = 1122.5;
    private const double PdfPointPerDeviceIndependentPixel = 72d / 96d;
    private const double PdfRenderDpi = 144d;

    private static readonly EnumeratedPrintQueueTypes[][] InstalledPrinterQueueTypeGroups =
    [
        [
            EnumeratedPrintQueueTypes.Local,
            EnumeratedPrintQueueTypes.Connections,
            EnumeratedPrintQueueTypes.Shared
        ],
        [EnumeratedPrintQueueTypes.DirectPrinting],
        [EnumeratedPrintQueueTypes.PushedMachineConnection],
        [EnumeratedPrintQueueTypes.PushedUserConnection],
        [EnumeratedPrintQueueTypes.WorkOffline]
    ];

    public static bool TryPrintDocument(
        IDocumentPaginatorSource document,
        string jobName,
        out string? errorMessage)
        => TryPrintDocument(document, jobName, new WpfSize(A4Width, A4Height), out errorMessage);

    public static bool TryPrintDocument(
        IDocumentPaginatorSource document,
        string jobName,
        WpfSize pageSize,
        out string? errorMessage,
        int? currentPageNumber = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        errorMessage = null;

        LocalPrintServer? printServer = null;
        (IReadOnlyList<PrintQueue> PrintQueues, PrintQueue? DefaultPrintQueue) LoadPrinterSnapshot()
        {
            printServer ??= new LocalPrintServer();
            return (LoadInstalledPrintQueues(printServer), TryGetDefaultPrintQueue(printServer));
        }

        (IReadOnlyList<PrintQueue> PrintQueues, PrintQueue? DefaultPrintQueue) LoadPrinterSnapshotSafely()
        {
            try
            {
                return LoadPrinterSnapshot();
            }
            catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or UnauthorizedAccessException)
            {
                AppLogger.Warn("PRINT", $"프린터 시스템을 열 수 없어 파일 저장 전용으로 인쇄창을 표시합니다: {ex.Message}");
                return (Array.Empty<PrintQueue>(), null);
            }
        }

        try
        {
            IReadOnlyList<PrintQueue> printQueues = Array.Empty<PrintQueue>();
            PrintQueue? defaultQueue = null;
            var printerSnapshot = LoadPrinterSnapshotSafely();
            printQueues = printerSnapshot.PrintQueues;
            defaultQueue = printerSnapshot.DefaultPrintQueue;

            var paginator = document.DocumentPaginator;
            paginator.PageSize = pageSize;
            var pageCount = ResolvePageCount(paginator);

            var dialog = new TradePrintWindow(
                printQueues,
                defaultQueue,
                pageCount,
                LoadPrinterSnapshotSafely,
                currentPageNumber,
                defaultFileBaseName: jobName)
            {
                Owner = ResolveActiveOwner()
            };

            if (dialog.ShowDialog() != true || dialog.PrintOptions is null)
                return false;

            paginator.PageSize = pageSize;
            var targetPaginator = BuildTargetPaginator(paginator, dialog.PrintOptions.PageNumbers, dialog.PrintOptions.ReversePageOrder, pageCount);
            if (dialog.PrintOptions.SaveToFile)
            {
                if (dialog.PrintOptions.FileFormat == TradePrintFileFormat.Pdf)
                    SaveDocumentAsPdf(targetPaginator, dialog.PrintOptions.OutputFilePath);
                else
                    SaveDocumentAsXps(targetPaginator, dialog.PrintOptions.OutputFilePath);
                return true;
            }

            if (dialog.PrintOptions.PrintQueue is null)
            {
                errorMessage = "인쇄할 프린터를 선택하세요. 프린터가 없거나 복합기 연결이 안 되면 PDF 저장 또는 파일 저장(XPS)을 사용하세요.";
                return false;
            }

            var copyExpandedPaginator = BuildCopyPaginator(targetPaginator, dialog.PrintOptions.CopyCount, dialog.PrintOptions.Collate);
            var driverCopyCount = ReferenceEquals(copyExpandedPaginator, targetPaginator)
                ? dialog.PrintOptions.CopyCount
                : 1;
            var printTicket = BuildPrintTicket(dialog.PrintOptions.PrintQueue, driverCopyCount, dialog.PrintOptions.Collate);
            var writer = PrintQueue.CreateXpsDocumentWriter(dialog.PrintOptions.PrintQueue);
            writer.Write(copyExpandedPaginator, printTicket);
            return true;
        }
        catch (PrintQueueException ex)
        {
            errorMessage = $"프린터 오류: {ex.Message}";
            return false;
        }
        catch (PrintSystemException ex)
        {
            errorMessage = $"인쇄 시스템 오류: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            errorMessage = $"인쇄 권한 오류: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = $"인쇄 중 오류가 발생했습니다: {ex.Message}";
            return false;
        }
        finally
        {
            printServer?.Dispose();
        }
    }

    private static IReadOnlyList<PrintQueue> LoadInstalledPrintQueues(LocalPrintServer printServer)
    {
        var queuesByName = new Dictionary<string, PrintQueue>(StringComparer.OrdinalIgnoreCase);

        foreach (var queueTypes in InstalledPrinterQueueTypeGroups)
        {
            try
            {
                foreach (var queue in printServer.GetPrintQueues(queueTypes))
                    AddQueue(queue);
            }
            catch (PrintSystemException ex)
            {
                var typeNames = string.Join(", ", queueTypes.Select(static type => type.ToString()));
                AppLogger.Warn("PRINT", $"프린터 목록 확인 실패({typeNames}): {ex.Message}");
            }
        }

        try
        {
            var defaultQueue = TryGetDefaultPrintQueue(printServer);
            if (defaultQueue is not null)
                AddQueue(defaultQueue);
        }
        catch (PrintSystemException ex)
        {
            AppLogger.Warn("PRINT", $"기본 프린터 확인 실패: {ex.Message}");
        }

        return queuesByName.Values
            .OrderBy(static queue => SafeRead(queue, static q => q.FullName), StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        void AddQueue(PrintQueue queue)
        {
            var key = SafeRead(queue, static q => q.FullName);
            if (string.IsNullOrWhiteSpace(key))
                key = SafeRead(queue, static q => q.Name);
            if (string.IsNullOrWhiteSpace(key))
                return;

            queuesByName.TryAdd(key, queue);
        }
    }

    private static PrintQueue? TryGetDefaultPrintQueue(LocalPrintServer printServer)
    {
        try
        {
            return printServer.DefaultPrintQueue;
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException)
        {
            AppLogger.Warn("PRINT", $"기본 프린터 확인 실패: {ex.Message}");
            return null;
        }
    }

    private static int ResolvePageCount(DocumentPaginator paginator)
    {
        try
        {
            paginator.ComputePageCount();
        }
        catch (Exception ex)
        {
            AppLogger.Warn("PRINT", $"인쇄 페이지 수 계산 실패: {ex.Message}");
        }

        return paginator.IsPageCountValid && paginator.PageCount > 0 ? paginator.PageCount : 0;
    }

    private static DocumentPaginator BuildTargetPaginator(
        DocumentPaginator source,
        IReadOnlyList<int>? pageNumbers,
        bool reversePageOrder,
        int pageCount)
    {
        var targetPages = pageNumbers is { Count: > 0 }
            ? pageNumbers.ToList()
            : Enumerable.Range(1, Math.Max(0, pageCount)).ToList();

        if (reversePageOrder)
            targetPages.Reverse();

        if (targetPages.Count == 0 || IsWholeDocumentInNaturalOrder(targetPages, pageCount))
            return source;

        return new PageSelectionDocumentPaginator(source, targetPages);
    }

    private static bool IsWholeDocumentInNaturalOrder(IReadOnlyList<int> pages, int pageCount)
    {
        if (pageCount <= 0 || pages.Count != pageCount)
            return false;

        for (var index = 0; index < pages.Count; index++)
        {
            if (pages[index] != index + 1)
                return false;
        }

        return true;
    }

    private static DocumentPaginator BuildCopyPaginator(
        DocumentPaginator source,
        int copyCount,
        bool collate)
    {
        if (copyCount <= 1)
            return source;

        var pageCount = ResolvePageCount(source);
        if (pageCount <= 0)
            return source;

        var normalizedCopyCount = Math.Clamp(copyCount, 1, 999);
        var pages = new List<int>(checked(pageCount * normalizedCopyCount));
        if (collate)
        {
            for (var copy = 0; copy < normalizedCopyCount; copy++)
            {
                for (var page = 1; page <= pageCount; page++)
                    pages.Add(page);
            }
        }
        else
        {
            for (var page = 1; page <= pageCount; page++)
            {
                for (var copy = 0; copy < normalizedCopyCount; copy++)
                    pages.Add(page);
            }
        }

        return new PageSelectionDocumentPaginator(source, pages);
    }

    private static PrintTicket BuildPrintTicket(PrintQueue printQueue, int copyCount, bool collate)
    {
        var printTicket = printQueue.UserPrintTicket ?? printQueue.DefaultPrintTicket ?? new PrintTicket();

        try
        {
            printTicket.CopyCount = Math.Clamp(copyCount, 1, 999);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or PrintSystemException)
        {
            AppLogger.Warn("PRINT", $"인쇄 매수 설정 실패: {ex.Message}");
        }

        try
        {
            printTicket.Collation = collate ? Collation.Collated : Collation.Uncollated;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or PrintSystemException)
        {
            AppLogger.Warn("PRINT", $"한 부씩 인쇄 설정 실패: {ex.Message}");
        }

        return printTicket;
    }

    private static void SaveDocumentAsXps(DocumentPaginator paginator, string? outputFilePath)
    {
        if (string.IsNullOrWhiteSpace(outputFilePath))
            throw new InvalidOperationException("저장할 파일 경로가 비어 있습니다.");

        var directory = Path.GetDirectoryName(outputFilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("저장할 폴더를 찾을 수 없습니다.");

        using var package = Package.Open(outputFilePath, FileMode.Create, FileAccess.ReadWrite);
        using var xpsDocument = new XpsDocument(package, CompressionOption.Maximum);
        var writer = XpsDocument.CreateXpsDocumentWriter(xpsDocument);
        writer.Write(paginator);
    }

    private static void SaveDocumentAsPdf(DocumentPaginator paginator, string? outputFilePath)
    {
        if (string.IsNullOrWhiteSpace(outputFilePath))
            throw new InvalidOperationException("저장할 PDF 파일 경로가 비어 있습니다.");

        var directory = Path.GetDirectoryName(outputFilePath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            throw new DirectoryNotFoundException("PDF를 저장할 폴더를 찾을 수 없습니다.");

        QuestPDF.Settings.License = LicenseType.Community;

        var pageCount = ResolvePageCount(paginator);
        if (pageCount <= 0)
            throw new InvalidOperationException("PDF로 저장할 문서 페이지가 없습니다.");

        var pageSize = paginator.PageSize;
        if (pageSize.Width <= 0 || pageSize.Height <= 0)
            pageSize = new WpfSize(A4Width, A4Height);

        var renderedPages = new List<byte[]>(pageCount);
        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            using var page = paginator.GetPage(pageIndex);
            if (page == DocumentPage.Missing)
                continue;

            renderedPages.Add(RenderDocumentPageToPng(page, pageSize));
        }

        if (renderedPages.Count == 0)
            throw new InvalidOperationException("PDF로 저장할 문서 페이지를 렌더링하지 못했습니다.");

        var pageWidthPoints = (float)(pageSize.Width * PdfPointPerDeviceIndependentPixel);
        var pageHeightPoints = (float)(pageSize.Height * PdfPointPerDeviceIndependentPixel);
        Document.Create(container =>
        {
            foreach (var pageImage in renderedPages)
            {
                container.Page(page =>
                {
                    page.Size(pageWidthPoints, pageHeightPoints, Unit.Point);
                    page.Margin(0);
                    page.Content().Image(pageImage).FitArea();
                });
            }
        }).GeneratePdf(outputFilePath);
    }

    private static byte[] RenderDocumentPageToPng(DocumentPage page, WpfSize pageSize)
    {
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(pageSize.Width * PdfRenderDpi / 96d));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(pageSize.Height * PdfRenderDpi / 96d));
        var visual = page.Visual;

        if (visual is UIElement element)
        {
            element.Measure(pageSize);
            element.Arrange(new Rect(pageSize));
            element.UpdateLayout();
        }

        var drawingVisual = new DrawingVisual();
        using (var context = drawingVisual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(pageSize));
            context.DrawRectangle(new VisualBrush(visual), null, new Rect(pageSize));
        }

        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            PdfRenderDpi,
            PdfRenderDpi,
            PixelFormats.Pbgra32);
        bitmap.Render(drawingVisual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Window? ResolveActiveOwner()
    {
        var current = Application.Current;
        if (current is null)
            return null;

        return current.Windows
                   .OfType<Window>()
                   .FirstOrDefault(static window => window.IsActive) ??
               current.MainWindow;
    }

    private static string SafeRead(PrintQueue queue, Func<PrintQueue, string?> reader)
    {
        try
        {
            return reader(queue) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private sealed class PageSelectionDocumentPaginator : DocumentPaginator
    {
        private readonly DocumentPaginator _source;
        private readonly IReadOnlyList<int> _pageNumbers;

        public PageSelectionDocumentPaginator(DocumentPaginator source, IReadOnlyList<int> pageNumbers)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _pageNumbers = pageNumbers ?? throw new ArgumentNullException(nameof(pageNumbers));
        }

        public override bool IsPageCountValid => true;

        public override int PageCount => _pageNumbers.Count;

        public override WpfSize PageSize
        {
            get => _source.PageSize;
            set => _source.PageSize = value;
        }

        public override IDocumentPaginatorSource Source => _source.Source;

        public override DocumentPage GetPage(int pageNumber)
        {
            if (pageNumber < 0 || pageNumber >= _pageNumbers.Count)
                return DocumentPage.Missing;

            return _source.GetPage(_pageNumbers[pageNumber] - 1);
        }
    }
}
