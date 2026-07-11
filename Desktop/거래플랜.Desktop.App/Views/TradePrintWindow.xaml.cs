using System.Diagnostics;
using System.IO;
using System.Printing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Views;

public partial class TradePrintWindow : Window
{
    private static readonly Brush InfoStatusBrush = CreateFrozenBrush("#90CAF9");
    private static readonly Brush SuccessStatusBrush = CreateFrozenBrush("#A5D6A7");
    private static readonly Brush WarningStatusBrush = CreateFrozenBrush("#FFCC80");
    private static readonly Brush ErrorStatusBrush = CreateFrozenBrush("#EF9A9A");

    private readonly int _pageCount;
    private readonly int? _currentPageNumber;
    private readonly string _defaultFileBaseName;
    private readonly Func<(IReadOnlyList<PrintQueue> PrintQueues, PrintQueue? DefaultPrintQueue)>? _printerRefreshProvider;
    private bool _isRefreshingPrinters;

    public TradePrintDialogResult? PrintOptions { get; private set; }

    public TradePrintWindow(
        IReadOnlyList<PrintQueue> printQueues,
        PrintQueue? defaultPrintQueue,
        int pageCount,
        Func<(IReadOnlyList<PrintQueue> PrintQueues, PrintQueue? DefaultPrintQueue)>? printerRefreshProvider = null,
        int? currentPageNumber = null,
        string? defaultFileBaseName = null)
    {
        ArgumentNullException.ThrowIfNull(printQueues);

        InitializeComponent();
        _pageCount = Math.Max(0, pageCount);
        _currentPageNumber = NormalizeCurrentPageNumber(currentPageNumber, _pageCount);
        _defaultFileBaseName = NormalizeDefaultFileBaseName(defaultFileBaseName);
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
        UpdateSelectedPrinterState();

        return items.Count;
    }

    private void OnPrinterSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateSelectedPrinterState();

    private void UpdateSelectedPrinterState()
    {
        if (GetSelectedPrinterItem() is not PrinterListItem item)
        {
            PrinterTypeTextBlock.Text = string.Empty;
            PrinterLocationTextBlock.Text = string.Empty;
            PrinterStatusTextBlock.Text = string.Empty;
            SetStatus(
                PrinterComboBox.Items.Count == 0
                    ? "등록된 프린터를 찾지 못했습니다. PDF 저장 또는 파일 저장(XPS)으로 문서를 저장한 뒤 복합기에서 출력하세요."
                    : "프린터가 선택되지 않았습니다. PDF 저장 또는 파일 저장(XPS)을 사용할 수 있습니다.",
                StatusTone.Warning);
            UpdatePrinterActionState();
            return;
        }

        PrinterTypeTextBlock.Text = item.TypeText;
        PrinterLocationTextBlock.Text = item.LocationText;
        PrinterStatusTextBlock.Text = item.StatusText;
        SetStatus(
            item.IsOffline
                ? "선택한 프린터가 오프라인입니다. 프린터 상태를 확인하거나 PDF 저장으로 대체 출력하세요."
                : "프린터와 인쇄 옵션을 확인한 뒤 인쇄를 누르세요.",
            item.IsOffline ? StatusTone.Warning : StatusTone.Info);
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
                SetStatus("프린터 이름을 확인할 수 없어 속성 창을 열 수 없습니다.", StatusTone.Error);
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
            SetStatus("프린터 속성 창을 열었습니다. 설정을 변경한 뒤 인쇄 옵션을 다시 확인하세요.", StatusTone.Success);
        }
        catch (Exception ex)
        {
            SetStatus("프린터 속성 창을 열 수 없습니다.", StatusTone.Error);
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
            SetStatus("Windows 프린터 관리 화면을 열었습니다. 복합기를 추가하거나 연결을 확인한 뒤 새로고침을 누르세요.", StatusTone.Info);
            return;
        }

        SetStatus("Windows 프린터 관리 화면을 열 수 없습니다. 제어판 > 장치 및 프린터에서 복합기 연결을 확인하세요.", StatusTone.Error);
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
            SetStatus("현재 화면에서는 프린터 목록을 다시 불러올 수 없습니다. 인쇄창을 다시 열어 확인하세요.", StatusTone.Warning);
            return;
        }

        var selectedQueueName = GetSelectedQueueName();
        _isRefreshingPrinters = true;
        UpdatePrinterActionState();
        SetStatus("프린터 목록을 다시 불러오는 중입니다...", StatusTone.Info);

        try
        {
            var snapshot = _printerRefreshProvider();
            var printQueues = snapshot.PrintQueues ?? Array.Empty<PrintQueue>();
            var printerCount = PopulatePrinters(printQueues, snapshot.DefaultPrintQueue, selectedQueueName);
            SetStatus(
                printerCount == 0
                    ? "새로고침 후에도 등록된 프린터를 찾지 못했습니다. PDF 저장 또는 파일 저장(XPS)으로 문서를 저장한 뒤 복합기에서 출력하세요."
                    : $"프린터 목록을 새로고침했습니다. {printerCount:N0}대 중 사용할 프린터를 선택하세요.",
                printerCount == 0 ? StatusTone.Warning : StatusTone.Success);
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or UnauthorizedAccessException)
        {
            SetStatus($"프린터 목록을 새로고침하지 못했습니다. PDF 저장 또는 파일 저장(XPS)을 사용하세요. ({ex.Message})", StatusTone.Error);
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

    private void OnCopyDiagnosticClick(object sender, RoutedEventArgs e)
    {
        try
        {
            SetClipboardTextWithRetry(BuildPrinterDiagnosticReport());
            if (GetSelectedPrinterItem() is not PrinterListItem item)
            {
                SetStatus(
                    PrinterComboBox.Items.Count == 0
                        ? "프린터 진단 정보를 복사했습니다. 현재 PC에 등록된 프린터가 없어 PDF/XPS fallback 안내도 함께 포함했습니다."
                        : "프린터 진단 정보를 복사했습니다. 프린터를 아직 선택하지 않은 상태도 함께 기록했습니다.",
                    StatusTone.Warning);
                return;
            }

            SetStatus(
                item.IsOffline
                    ? $"프린터 진단 정보를 복사했습니다. '{item.DisplayName}'은(는) 오프라인으로 기록되었습니다."
                    : $"프린터 진단 정보를 클립보드에 복사했습니다. '{item.DisplayName}' 상태를 문의에 그대로 붙여넣으세요.",
                item.IsOffline ? StatusTone.Warning : StatusTone.Success);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or InvalidOperationException)
        {
            AppLogger.Warn("PRINT", $"프린터 진단 복사 실패: {ex.Message}");
            SetStatus($"프린터 진단 정보를 클립보드에 복사하지 못했습니다: {ex.Message}", StatusTone.Error);
            MessageBox.Show(
                this,
                $"프린터 진단 정보를 클립보드에 복사하지 못했습니다.{Environment.NewLine}{ex.Message}",
                "프린터 진단 복사",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private static void SetClipboardTextWithRetry(string text)
    {
        const int maxAttempts = 5;
        const int retryDelayMilliseconds = 80;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return;
            }
            catch (Exception ex) when (
                (ex is COMException or ExternalException or InvalidOperationException) &&
                attempt < maxAttempts)
            {
                Thread.Sleep(retryDelayMilliseconds);
            }
        }
    }

    private void OnPrintDiagnosticClick(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedPrinterItem();
        if (item is null)
        {
            SetStatus("1쪽 테스트 인쇄를 보낼 프린터가 없습니다. 프린터 연결 후 새로고침하거나 PDF 저장/파일 저장(XPS)을 사용하세요.", StatusTone.Warning);
            return;
        }

        if (item.IsOffline)
        {
            SetStatus($"선택한 프린터 '{item.DisplayName}'이(가) 오프라인이라 1쪽 테스트 인쇄를 보내지 않았습니다. 프린터 전원/네트워크/드라이버를 확인하세요.", StatusTone.Warning);
            return;
        }

        SetStatus($"'{item.DisplayName}'으로 1쪽 테스트 인쇄를 보내는 중입니다...", StatusTone.Info);
        if (TradePrintExecutor.TryPrintDiagnosticPage(item.Queue, out var errorMessage))
        {
            SetStatus($"'{item.DisplayName}'으로 1쪽 테스트 인쇄를 보냈습니다. 출력이 없으면 진단 복사 결과를 공유하거나 PDF/XPS fallback을 사용하세요.", StatusTone.Success);
            return;
        }

        SetStatus($"1쪽 테스트 인쇄를 보내지 못했습니다. {errorMessage}", StatusTone.Error);
    }

    private void SaveToFile(TradePrintFileFormat fileFormat)
    {
        var extension = fileFormat == TradePrintFileFormat.Pdf ? ".pdf" : ".xps";
        var defaultFileName = MakeSafeFileName($"{_defaultFileBaseName}-{DateTime.Now:yyyyMMdd-HHmm}{extension}");
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
        SetStatus(message, StatusTone.Error);
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
        var hasPrinter = GetSelectedPrinterItem() is not null;
        PropertiesButton.IsEnabled = hasPrinter;
        PrintButton.IsEnabled = hasPrinter;
        PrintDiagnosticButton.IsEnabled = hasPrinter;
        CopyDiagnosticButton.IsEnabled = !_isRefreshingPrinters;
        RefreshPrintersButton.IsEnabled = _printerRefreshProvider is not null && !_isRefreshingPrinters;
    }

    private PrinterListItem? GetSelectedPrinterItem()
        => PrinterComboBox.SelectedItem as PrinterListItem;

    private string GetSelectedQueueName()
    {
        if (GetSelectedPrinterItem() is not PrinterListItem item)
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

    private static string NormalizeDefaultFileBaseName(string? defaultFileBaseName)
    {
        var normalized = (defaultFileBaseName ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? "출력서류"
            : normalized;
    }

    private static string MakeSafeFileName(string fileName)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalidChar, '-');

        return fileName;
    }

    private string BuildPrinterDiagnosticReport()
    {
        var selectedItem = GetSelectedPrinterItem();
        var pageMode = GetPageModeSummary();
        var report = new StringBuilder();
        report.AppendLine("거래플랜 인쇄 진단");
        report.AppendLine($"생성 시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"PC 이름: {Environment.MachineName}");
        report.AppendLine($"사용자: {Environment.UserName}");
        report.AppendLine($"등록 프린터 수: {PrinterComboBox.Items.Count:N0}");
        report.AppendLine($"선택 프린터: {selectedItem?.DisplayName ?? (PrinterComboBox.Items.Count == 0 ? "등록된 프린터 없음" : "선택 안 함")}");
        report.AppendLine($"선택 프린터 상태: {selectedItem?.StatusText ?? (PrinterComboBox.Items.Count == 0 ? "등록된 프린터 없음" : "선택 안 함")}");
        report.AppendLine($"선택 프린터 위치: {selectedItem?.LocationText ?? "-"}");
        report.AppendLine($"선택 프린터 종류: {selectedItem?.TypeText ?? "-"}");
        report.AppendLine($"선택 프린터 오프라인: {(selectedItem?.IsOffline == true ? "예" : "아니오")}");
        report.AppendLine($"문서 페이지 수: {(_pageCount > 0 ? $"{_pageCount:N0}쪽" : "확인 불가")}");
        report.AppendLine($"현재 페이지: {(_currentPageNumber.HasValue ? $"{_currentPageNumber.Value:N0}쪽" : "확인 불가")}");
        report.AppendLine($"페이지 선택: {pageMode}");
        report.AppendLine($"인쇄 매수: {ReadCopyCountOrDefault():N0}");
        report.AppendLine($"한 부씩 인쇄: {(CollateCheckBox.IsChecked == true ? "예" : "아니오")}");
        report.AppendLine($"역방향 인쇄: {(ReverseOrderCheckBox.IsChecked == true ? "예" : "아니오")}");
        report.AppendLine($"현재 안내 메시지: {StatusTextBlock.Text}");
        report.AppendLine("fallback: PDF 저장 / 파일 저장(XPS)");
        return report.ToString().TrimEnd();
    }

    private string GetPageModeSummary()
    {
        if (CurrentPageRadioButton.IsChecked == true)
            return _currentPageNumber.HasValue ? $"현재 페이지 {_currentPageNumber.Value:N0}쪽" : "현재 페이지 (확인 불가)";

        if (PageRangeRadioButton.IsChecked == true)
        {
            var pageRange = PageRangeTextBox.Text.Trim();
            return string.IsNullOrWhiteSpace(pageRange) ? "페이지 범위 (입력 없음)" : $"페이지 범위 {pageRange}";
        }

        return "모든 페이지";
    }

    private void SetStatus(string message, StatusTone tone)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = tone switch
        {
            StatusTone.Success => SuccessStatusBrush,
            StatusTone.Warning => WarningStatusBrush,
            StatusTone.Error => ErrorStatusBrush,
            _ => InfoStatusBrush
        };
    }

    private static Brush CreateFrozenBrush(string colorCode)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(colorCode)!;
        brush.Freeze();
        return brush;
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

    private enum StatusTone
    {
        Info = 0,
        Success = 1,
        Warning = 2,
        Error = 3
    }
}
