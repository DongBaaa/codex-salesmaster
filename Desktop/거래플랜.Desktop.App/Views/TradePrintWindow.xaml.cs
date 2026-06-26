using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Windows;
using Microsoft.Win32;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Views;

public partial class TradePrintWindow : Window
{
    private readonly int _pageCount;
    private readonly int? _currentPageNumber;
    private readonly Func<(IReadOnlyList<PrintQueue> PrintQueues, PrintQueue? DefaultPrintQueue)>? _printerRefreshProvider;
    private bool _isRefreshingPrinters;

    public TradePrintDialogResult? PrintOptions { get; private set; }

    public TradePrintWindow(
        IReadOnlyList<PrintQueue> printQueues,
        PrintQueue? defaultPrintQueue,
        int pageCount,
        Func<(IReadOnlyList<PrintQueue> PrintQueues, PrintQueue? DefaultPrintQueue)>? printerRefreshProvider = null,
        int? currentPageNumber = null)
    {
        ArgumentNullException.ThrowIfNull(printQueues);

        InitializeComponent();
        _pageCount = Math.Max(0, pageCount);
        _currentPageNumber = NormalizeCurrentPageNumber(currentPageNumber, _pageCount);
        _printerRefreshProvider = printerRefreshProvider;
        ConfigureCurrentPageOption();
        PopulatePrinters(printQueues, defaultPrintQueue);
        PageCountTextBlock.Text = _pageCount > 0
            ? $"문서 총 {_pageCount:N0}쪽"
            : "문서 페이지 수를 아직 확인하지 못했습니다.";
    }

    private void ConfigureCurrentPageOption()
    {
        if (_currentPageNumber.HasValue)
        {
            CurrentPageRadioButton.Content = $"현재 페이지 ({_currentPageNumber.Value:N0}쪽)";
            CurrentPageRadioButton.IsEnabled = true;
            CurrentPageRadioButton.ToolTip = $"{_currentPageNumber.Value:N0}쪽만 인쇄합니다.";
            return;
        }

        CurrentPageRadioButton.Content = "현재 페이지";
        CurrentPageRadioButton.IsEnabled = false;
        CurrentPageRadioButton.ToolTip = "미리보기 현재 페이지를 확인할 수 없어 사용할 수 없습니다.";
    }

    private int PopulatePrinters(
        IReadOnlyList<PrintQueue> printQueues,
        PrintQueue? defaultPrintQueue,
        string? preferredQueueName = null)
    {
        var defaultName = SafeRead(defaultPrintQueue, q => q.FullName);
        var items = printQueues
            .Where(static queue => queue is not null)
            .Select(queue => new PrinterListItem(queue, IsSameQueue(queue, defaultName)))
            .OrderByDescending(static item => item.IsDefault)
            .ThenBy(static item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        PrinterListItem? preferredItem = null;
        if (!string.IsNullOrWhiteSpace(preferredQueueName))
            preferredItem = items.FirstOrDefault(item => IsSameQueue(item.Queue, preferredQueueName));

        PrinterComboBox.ItemsSource = items;
        PrinterComboBox.SelectedItem =
            preferredItem ??
            items.FirstOrDefault(static item => item.IsDefault) ??
            items.FirstOrDefault();
        UpdatePrinterActionState();

        if (items.Count == 0)
        {
            StatusTextBlock.Text = "등록된 프린터를 찾지 못했습니다. PDF 저장 또는 파일 저장(XPS)으로 문서를 저장한 뒤 복합기에서 출력하세요.";
        }

        return items.Count;
    }

    private void OnPrinterSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PrinterComboBox.SelectedItem is not PrinterListItem item)
        {
            PrinterTypeTextBlock.Text = string.Empty;
            PrinterLocationTextBlock.Text = string.Empty;
            PrinterStatusTextBlock.Text = string.Empty;
            StatusTextBlock.Text = "프린터가 선택되지 않았습니다. PDF 저장 또는 파일 저장(XPS)을 사용할 수 있습니다.";
            UpdatePrinterActionState();
            return;
        }

        PrinterTypeTextBlock.Text = item.TypeText;
        PrinterLocationTextBlock.Text = item.LocationText;
        PrinterStatusTextBlock.Text = item.StatusText;
        StatusTextBlock.Text = item.IsOffline
            ? "선택한 프린터가 오프라인입니다. 프린터 상태를 확인하거나 PDF 저장으로 대체 출력하세요."
            : "프린터와 인쇄 옵션을 확인한 뒤 확인을 누르세요.";
        UpdatePrinterActionState();
    }

    private void OnPrinterPropertiesClick(object sender, RoutedEventArgs e)
    {
        if (PrinterComboBox.SelectedItem is not PrinterListItem item)
            return;

        try
        {
            var printerName = SafeRead(item.Queue, static q => q.FullName);
            if (string.IsNullOrWhiteSpace(printerName))
                printerName = SafeRead(item.Queue, static q => q.Name);
            if (string.IsNullOrWhiteSpace(printerName))
            {
                StatusTextBlock.Text = "프린터 이름을 확인할 수 없어 속성 창을 열 수 없습니다.";
                return;
            }

            var safePrinterName = printerName.Replace("\"", "\\\"", StringComparison.Ordinal);
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = $"printui.dll,PrintUIEntry /p /n \"{safePrinterName}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            StatusTextBlock.Text = "프린터 속성 창을 열었습니다. 설정을 변경한 뒤 인쇄 옵션을 다시 확인하세요.";
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = "프린터 속성 창을 열 수 없습니다.";
            MessageBox.Show(
                this,
                $"프린터 속성 창을 열 수 없습니다.{Environment.NewLine}{ex.Message}",
                "프린터 속성",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnOpenPrinterManagementClick(object sender, RoutedEventArgs e)
    {
        if (TryOpenPrinterManagement())
        {
            StatusTextBlock.Text = "Windows 프린터 관리 화면을 열었습니다. 복합기를 추가하거나 연결을 확인한 뒤 새로고침을 누르세요.";
            return;
        }

        StatusTextBlock.Text = "Windows 프린터 관리 화면을 열 수 없습니다. 제어판 > 장치 및 프린터에서 복합기 연결을 확인하세요.";
        MessageBox.Show(
            this,
            "Windows 프린터 관리 화면을 열 수 없습니다.\n제어판 > 장치 및 프린터에서 복합기 연결을 확인한 뒤 거래플랜 인쇄창에서 새로고침을 누르세요.",
            "프린터 관리",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static bool TryOpenPrinterManagement()
    {
        return TryStartShellProcess("ms-settings:printers") ||
               TryStartShellProcess("control.exe", "printers");
    }

    private static bool TryStartShellProcess(string fileName, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or System.ComponentModel.Win32Exception)
        {
            AppLogger.Warn("PRINT", $"프린터 관리 화면 실행 실패({fileName}): {ex.Message}");
            return false;
        }
    }

    private void OnRefreshPrintersClick(object sender, RoutedEventArgs e)
    {
        if (_printerRefreshProvider is null)
        {
            StatusTextBlock.Text = "현재 화면에서는 프린터 목록을 다시 불러올 수 없습니다. 인쇄창을 다시 열어 확인하세요.";
            return;
        }

        var selectedQueueName = GetSelectedQueueName();
        _isRefreshingPrinters = true;
        UpdatePrinterActionState();
        StatusTextBlock.Text = "프린터 목록을 다시 불러오는 중입니다...";

        try
        {
            var snapshot = _printerRefreshProvider();
            var printQueues = snapshot.PrintQueues ?? Array.Empty<PrintQueue>();
            var printerCount = PopulatePrinters(printQueues, snapshot.DefaultPrintQueue, selectedQueueName);
            StatusTextBlock.Text = printerCount == 0
                ? "새로고침 후에도 등록된 프린터를 찾지 못했습니다. PDF 저장 또는 파일 저장(XPS)으로 문서를 저장한 뒤 복합기에서 출력하세요."
                : $"프린터 목록을 새로고침했습니다. {printerCount:N0}대 중 사용할 프린터를 선택하세요.";
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or UnauthorizedAccessException)
        {
            StatusTextBlock.Text = $"프린터 목록을 새로고침하지 못했습니다. PDF 저장 또는 파일 저장(XPS)을 사용하세요. ({ex.Message})";
        }
        finally
        {
            _isRefreshingPrinters = false;
            UpdatePrinterActionState();
        }
    }

    private void OnPageModeChecked(object sender, RoutedEventArgs e)
    {
        if (PageRangeTextBox is null)
            return;

        PageRangeTextBox.IsEnabled = PageRangeRadioButton.IsChecked == true;
        if (PageRangeTextBox.IsEnabled)
            PageRangeTextBox.Focus();
    }

    private void OnCopyCountIncreaseClick(object sender, RoutedEventArgs e)
        => SetCopyCount(ReadCopyCountOrDefault() + 1);

    private void OnCopyCountDecreaseClick(object sender, RoutedEventArgs e)
        => SetCopyCount(Math.Max(1, ReadCopyCountOrDefault() - 1));

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (TryBuildPrintOptions(saveToFile: false, outputFilePath: null, TradePrintFileFormat.Xps, out var options))
        {
            PrintOptions = options;
            DialogResult = true;
        }
    }

    private void OnSaveFileClick(object sender, RoutedEventArgs e)
        => SaveToFile(TradePrintFileFormat.Xps);

    private void OnSavePdfClick(object sender, RoutedEventArgs e)
        => SaveToFile(TradePrintFileFormat.Pdf);

    private void SaveToFile(TradePrintFileFormat fileFormat)
    {
        var extension = fileFormat == TradePrintFileFormat.Pdf ? ".pdf" : ".xps";
        var defaultFileName = MakeSafeFileName($"거래플랜-인쇄문서-{DateTime.Now:yyyyMMdd-HHmm}{extension}");
        var dialog = new SaveFileDialog
        {
            Title = "인쇄 문서 파일 저장",
            Filter = fileFormat == TradePrintFileFormat.Pdf
                ? "PDF 문서 (*.pdf)|*.pdf"
                : "XPS 문서 (*.xps)|*.xps",
            FileName = defaultFileName,
            AddExtension = true,
            DefaultExt = extension,
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (TryBuildPrintOptions(saveToFile: true, dialog.FileName, fileFormat, out var options))
        {
            PrintOptions = options;
            DialogResult = true;
        }
    }

    private bool TryBuildPrintOptions(
        bool saveToFile,
        string? outputFilePath,
        TradePrintFileFormat fileFormat,
        out TradePrintDialogResult? options)
    {
        options = null;
        PrinterListItem? item = null;
        if (!saveToFile)
        {
            item = PrinterComboBox.SelectedItem as PrinterListItem;
            if (item is null)
            {
                ShowValidationError("인쇄할 프린터를 선택하세요. 프린터가 없거나 복합기 연결이 안 되면 PDF 저장 또는 파일 저장(XPS)을 사용하세요.");
                return false;
            }
        }

        if (saveToFile)
        {
            if (string.IsNullOrWhiteSpace(outputFilePath))
            {
                ShowValidationError("저장할 파일 경로를 선택하세요.");
                return false;
            }

            var directory = Path.GetDirectoryName(outputFilePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                ShowValidationError("저장할 폴더를 찾을 수 없습니다.");
                return false;
            }
        }

        if (!int.TryParse(CopyCountTextBox.Text.Trim(), out var copyCount) || copyCount < 1 || copyCount > 999)
        {
            ShowValidationError("인쇄 매수는 1~999 사이의 숫자로 입력하세요.");
            CopyCountTextBox.Focus();
            CopyCountTextBox.SelectAll();
            return false;
        }

        IReadOnlyList<int>? pageNumbers = null;
        if (CurrentPageRadioButton.IsChecked == true)
        {
            if (!_currentPageNumber.HasValue)
            {
                ShowValidationError("현재 페이지 번호를 확인할 수 없습니다. 모든 페이지 또는 페이지 범위를 선택하세요.");
                return false;
            }

            pageNumbers = [_currentPageNumber.Value];
        }
        else if (PageRangeRadioButton.IsChecked == true)
        {
            if (!TradePrintPageRangeParser.TryParse(PageRangeTextBox.Text, _pageCount, out var parsedPages, out var errorMessage))
            {
                ShowValidationError(errorMessage ?? "페이지 범위를 확인하세요.");
                PageRangeTextBox.Focus();
                PageRangeTextBox.SelectAll();
                return false;
            }

            pageNumbers = parsedPages;
        }

        if (ReverseOrderCheckBox.IsChecked == true && _pageCount <= 0)
        {
            ShowValidationError("문서 페이지 수를 확인할 수 없어 역방향 인쇄를 사용할 수 없습니다.");
            return false;
        }

        options = new TradePrintDialogResult(
            saveToFile ? null : item!.Queue,
            copyCount,
            CollateCheckBox.IsChecked == true,
            pageNumbers,
            ReverseOrderCheckBox.IsChecked == true,
            _currentPageNumber,
            saveToFile,
            outputFilePath,
            fileFormat);
        return true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ShowValidationError(string message)
    {
        StatusTextBlock.Text = message;
        MessageBox.Show(
            this,
            message,
            "인쇄 옵션 확인",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private int ReadCopyCountOrDefault()
        => int.TryParse(CopyCountTextBox.Text.Trim(), out var copyCount) ? copyCount : 1;

    private void SetCopyCount(int copyCount)
        => CopyCountTextBox.Text = Math.Clamp(copyCount, 1, 999).ToString("N0").Replace(",", string.Empty, StringComparison.Ordinal);

    private void UpdatePrinterActionState()
    {
        // 속성/직접 인쇄 버튼은 선택된 프린터가 있을 때만 동작하도록 유지한다.
        var hasPrinter = PrinterComboBox.SelectedItem is PrinterListItem;
        PropertiesButton.IsEnabled = hasPrinter;
        PrintButton.IsEnabled = hasPrinter;
        RefreshPrintersButton.IsEnabled = _printerRefreshProvider is not null && !_isRefreshingPrinters;
    }

    private string GetSelectedQueueName()
    {
        if (PrinterComboBox.SelectedItem is not PrinterListItem item)
            return string.Empty;

        var queueName = SafeRead(item.Queue, static q => q.FullName);
        if (string.IsNullOrWhiteSpace(queueName))
            queueName = SafeRead(item.Queue, static q => q.Name);
        return queueName;
    }

    private static bool IsSameQueue(PrintQueue queue, string? defaultFullName)
    {
        if (string.IsNullOrWhiteSpace(defaultFullName))
            return false;

        return string.Equals(SafeRead(queue, q => q.FullName), defaultFullName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(SafeRead(queue, q => q.Name), defaultFullName, StringComparison.OrdinalIgnoreCase);
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

    private static bool SafeReadBool(PrintQueue queue, Func<PrintQueue, bool> reader)
    {
        try
        {
            return reader(queue);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int? NormalizeCurrentPageNumber(int? currentPageNumber, int pageCount)
    {
        if (!currentPageNumber.HasValue || pageCount <= 0)
            return null;

        return currentPageNumber.Value >= 1 && currentPageNumber.Value <= pageCount
            ? currentPageNumber.Value
            : null;
    }

    private static string MakeSafeFileName(string fileName)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalidChar, '-');

        return fileName;
    }

    private sealed class PrinterListItem
    {
        public PrinterListItem(PrintQueue queue, bool isDefault)
        {
            Queue = queue;
            IsDefault = isDefault;

            var queueName = SafeRead(queue, static q => q.FullName);
            if (string.IsNullOrWhiteSpace(queueName))
                queueName = SafeRead(queue, static q => q.Name);

            DisplayName = isDefault ? $"{queueName} (기본)" : queueName;

            var shareName = SafeRead(queue, static q => q.ShareName);
            TypeText = string.IsNullOrWhiteSpace(shareName)
                ? queueName
                : $"{queueName} / 공유명: {shareName}";

            var location = SafeRead(queue, static q => q.Location);
            var comment = SafeRead(queue, static q => q.Comment);
            LocationText = string.IsNullOrWhiteSpace(location)
                ? (string.IsNullOrWhiteSpace(comment) ? "-" : comment)
                : location;

            var status = SafeRead(queue, static q => q.QueueStatus.ToString());
            IsOffline = SafeReadBool(queue, static q => q.IsOffline);
            if (IsOffline)
                status = string.IsNullOrWhiteSpace(status) ? "오프라인" : $"{status}, 오프라인";
            StatusText = string.IsNullOrWhiteSpace(status) || status == "None" ? "준비" : status;
        }

        public PrintQueue Queue { get; }
        public bool IsDefault { get; }
        public string DisplayName { get; }
        public string TypeText { get; }
        public string LocationText { get; }
        public string StatusText { get; }
        public bool IsOffline { get; }
    }
}
