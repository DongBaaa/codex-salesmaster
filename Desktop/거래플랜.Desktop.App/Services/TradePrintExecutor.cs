using System.IO;
using System.IO.Packaging;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
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

    private static readonly EnumeratedPrintQueueTypes[] InstalledPrinterQueueTypes =
    [
        EnumeratedPrintQueueTypes.Local,
        EnumeratedPrintQueueTypes.Connections,
        EnumeratedPrintQueueTypes.Shared,
        EnumeratedPrintQueueTypes.DirectPrinting,
        EnumeratedPrintQueueTypes.PushedMachineConnection,
        EnumeratedPrintQueueTypes.PushedUserConnection,
        EnumeratedPrintQueueTypes.WorkOffline,
        EnumeratedPrintQueueTypes.Queued,
        EnumeratedPrintQueueTypes.PublishedInDirectoryServices,
        EnumeratedPrintQueueTypes.Fax
    ];

    public static bool TryPrintDocument(
        IDocumentPaginatorSource document,
        string jobName,
        out string? errorMessage)
        => TryPrintDocument(document, jobName, new WpfSize(A4Width, A4Height), out errorMessage);

    public static bool TryPrintDiagnosticPage(
        PrintQueue? printQueue,
        out string? errorMessage)
        => TryPrintDiagnosticPage(
            printQueue,
            DateTimeOffset.Now,
            static (queue, document, printTicket) =>
            {
                var writer = PrintQueue.CreateXpsDocumentWriter(queue);
                writer.Write(document.DocumentPaginator, printTicket);
            },
            out errorMessage);

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

    private static bool TryPrintDiagnosticPage(
        PrintQueue? printQueue,
        DateTimeOffset generatedAt,
        Action<PrintQueue, FixedDocument, PrintTicket> sendDocument,
        out string? errorMessage)
    {
        errorMessage = null;
        if (printQueue is null)
        {
            errorMessage = "1쪽 테스트 인쇄를 보낼 프린터를 선택하세요.";
            return false;
        }

        var printerName = ResolveQueueName(printQueue);
        if (string.IsNullOrWhiteSpace(printerName))
            printerName = "선택 프린터";

        if (SafeReadBool(printQueue, static queue => queue.IsOffline))
        {
            errorMessage = $"선택한 프린터 '{printerName}'이(가) 오프라인입니다. 프린터 전원/네트워크/드라이버를 확인하세요.";
            return false;
        }

        try
        {
            var document = BuildDiagnosticDocument(printerName, printQueue, generatedAt);
            var printTicket = BuildPrintTicket(printQueue, 1, true);
            sendDocument(printQueue, document, printTicket);
            return true;
        }
        catch (PrintQueueException ex)
        {
            errorMessage = $"1쪽 테스트 인쇄 출력 오류: {ex.Message}";
            return false;
        }
        catch (PrintSystemException ex)
        {
            errorMessage = $"1쪽 테스트 인쇄 시스템 오류: {ex.Message}";
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            errorMessage = $"1쪽 테스트 인쇄 권한 오류: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            errorMessage = $"1쪽 테스트 인쇄를 보내지 못했습니다: {ex.Message}";
            return false;
        }
    }

    private static IReadOnlyList<PrintQueue> LoadInstalledPrintQueues(LocalPrintServer printServer)
    {
        var queuesByName = new Dictionary<string, PrintQueue>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var queue in printServer.GetPrintQueues(InstalledPrinterQueueTypes))
                AddQueue(queue);
        }
        catch (PrintSystemException ex)
        {
            AppLogger.Warn("PRINT", $"프린터 전체 목록 확인 실패: {ex.Message}");
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

    private static PrintTicket BuildPrintTicket(PrintQueue? printQueue, int copyCount, bool collate)
    {
        var printTicket = TryGetPrintTicket(printQueue) ?? new PrintTicket();

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

    private static PrintTicket? TryGetPrintTicket(PrintQueue? printQueue)
    {
        if (printQueue is null)
            return null;

        try
        {
            return printQueue.UserPrintTicket ?? printQueue.DefaultPrintTicket;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("PRINT", $"프린터 PrintTicket 확인 실패: {ex.Message}");
            return null;
        }
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

    private static FixedDocument BuildDiagnosticDocument(
        string printerName,
        PrintQueue? printQueue,
        DateTimeOffset generatedAt)
    {
        var document = new FixedDocument();
        document.DocumentPaginator.PageSize = new WpfSize(A4Width, A4Height);

        var statusText = SafeRead(printQueue, static queue => queue.QueueStatus.ToString());
        if (string.IsNullOrWhiteSpace(statusText) || statusText == "None")
            statusText = "준비";

        var locationText = SafeRead(printQueue, static queue => queue.Location);
        if (string.IsNullOrWhiteSpace(locationText))
            locationText = "-";

        var shareName = SafeRead(printQueue, static queue => queue.ShareName);
        var printerTypeText = string.IsNullOrWhiteSpace(shareName)
            ? printerName
            : $"{printerName} / 공유명: {shareName}";

        var panel = new StackPanel
        {
            Margin = new Thickness(56, 56, 56, 40)
        };
        panel.Children.Add(new TextBlock
        {
            Text = "거래플랜 프린터 진단 페이지",
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 28,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.Black
        });
        panel.Children.Add(new TextBlock
        {
            Text = "이 페이지가 정상적으로 출력되면 선택 프린터와 거래플랜의 기본 인쇄 연결은 동작 중입니다.",
            FontFamily = new FontFamily("맑은 고딕"),
            FontSize = 14,
            Margin = new Thickness(0, 10, 0, 20),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Black
        });

        foreach (var line in new[]
                 {
                     $"생성 시각: {generatedAt:yyyy-MM-dd HH:mm:ss zzz}",
                     $"PC 이름: {Environment.MachineName}",
                     $"사용자: {Environment.UserName}",
                     $"프린터: {printerName}",
                     $"프린터 상태: {statusText}",
                     $"프린터 위치: {locationText}",
                     $"프린터 종류: {printerTypeText}",
                     $"오프라인 여부: {(SafeReadBool(printQueue, static queue => queue.IsOffline) ? "예" : "아니오")}",
                     "안내: 출력이 없거나 지연되면 거래플랜 인쇄창에서 진단 복사를 눌러 상태를 공유하세요.",
                     "fallback: PDF 저장 / 파일 저장(XPS)"
                 })
        {
            panel.Children.Add(new TextBlock
            {
                Text = line,
                FontFamily = new FontFamily("맑은 고딕"),
                FontSize = 15,
                Margin = new Thickness(0, 0, 0, 10),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Black
            });
        }

        var page = new FixedPage
        {
            Width = A4Width,
            Height = A4Height,
            Background = Brushes.White
        };
        page.Children.Add(panel);

        var content = new PageContent();
        ((System.Windows.Markup.IAddChild)content).AddChild(page);
        document.Pages.Add(content);
        document.DocumentPaginator.PageSize = new WpfSize(A4Width, A4Height);
        return document;
    }

    private static string ResolveQueueName(PrintQueue? printQueue)
    {
        var queueName = SafeRead(printQueue, static queue => queue.FullName);
        if (string.IsNullOrWhiteSpace(queueName))
            queueName = SafeRead(printQueue, static queue => queue.Name);

        return queueName;
    }

    private static string SafeRead(PrintQueue? queue, Func<PrintQueue, string?> reader)
    {
        if (queue is null)
            return string.Empty;

        try
        {
            return reader(queue) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static bool SafeReadBool(PrintQueue? queue, Func<PrintQueue, bool> reader)
    {
        if (queue is null)
            return false;

        try
        {
            return reader(queue);
        }
        catch (Exception)
        {
            return false;
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
