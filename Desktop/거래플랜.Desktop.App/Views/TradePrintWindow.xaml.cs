using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Views;

public partial class TradePrintWindow : Window
{
    private readonly int _pageCount;
    private readonly int? _currentPageNumber;
    private readonly string _defaultFileBaseName;
    private readonly Func<PrinterCatalogSnapshot>? _printerCatalogProvider;
    private bool _isRefreshingPrinters;

    public TradePrintDialogResult? PrintOptions { get; private set; }

    public TradePrintWindow(
        PrinterCatalogSnapshot printerCatalog,
        int pageCount,
        Func<PrinterCatalogSnapshot>? printerCatalogProvider = null,
        int? currentPageNumber = null,
        string? defaultFileBaseName = null)
    {
        ArgumentNullException.ThrowIfNull(printerCatalog);

        InitializeComponent();
        _pageCount = Math.Max(0, pageCount);
        _currentPageNumber = NormalizeCurrentPageNumber(currentPageNumber, _pageCount);
        _defaultFileBaseName = NormalizeDefaultFileBaseName(defaultFileBaseName);
        _printerCatalogProvider = printerCatalogProvider;
        ConfigureCurrentPageOption();
        PopulatePrinters(printerCatalog);
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
        PrinterCatalogSnapshot printerCatalog,
        string? preferredQueueName = null)
    {
        var items = printerCatalog.Printers
            .Where(static printer => !string.IsNullOrWhiteSpace(printer.QueueName))
            .Select(static printer => new PrinterListItem(printer))
            .OrderByDescending(static item => item.IsDefault)
            .ThenBy(static item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        PrinterListItem? preferredItem = null;
        if (!string.IsNullOrWhiteSpace(preferredQueueName))
            preferredItem = items.FirstOrDefault(item => IsSameQueue(item.QueueName, preferredQueueName));

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
            SetStatus(
                PrinterComboBox.Items.Count == 0
                    ? "등록된 프린터를 찾지 못했습니다. PDF 저장 또는 파일 저장(XPS)으로 문서를 저장한 뒤 복합기에서 출력하세요."
                    : "프린터가 선택되지 않았습니다. PDF 저장 또는 파일 저장(XPS)을 사용할 수 있습니다.",
                StatusTone.Warning);
            UpdatePrinterActionState();
            return;
        }

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
            var printerName = item.QueueName;
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
        => UiTaskHelper.Run(
            this,
            RefreshPrintersAsync,
            "PRINT",
            "프린터 목록 새로고침");

    private async Task RefreshPrintersAsync()
    {
        if (_printerCatalogProvider is null)
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
            var snapshot = await Task.Run(_printerCatalogProvider);
            var printerCount = PopulatePrinters(snapshot, selectedQueueName);
            SetStatus(
                printerCount == 0
                    ? "새로고침 후에도 등록된 프린터를 찾지 못했습니다. PDF 저장 또는 파일 저장(XPS)으로 문서를 저장한 뒤 복합기에서 출력하세요."
                    : $"프린터 목록을 새로고침했습니다. {printerCount:N0}대 중 사용할 프린터를 선택하세요.",
                printerCount == 0 ? StatusTone.Warning : StatusTone.Success);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException)
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

        if (DialogWindowCloseHelper.ShowDialog(dialog, this) != true)
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

        options = new TradePrintDialogResult(
            saveToFile ? null : item!.QueueName,
            copyCount,
            CollateCheckBox.IsChecked == true,
            pageNumbers,
            false,
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
        RefreshPrintersButton.IsEnabled = _printerCatalogProvider is not null && !_isRefreshingPrinters;
    }

    private PrinterListItem? GetSelectedPrinterItem()
        => PrinterComboBox.SelectedItem as PrinterListItem;

    private string GetSelectedQueueName()
    {
        if (GetSelectedPrinterItem() is not PrinterListItem item)
            return string.Empty;

        return item.QueueName;
    }

    private static bool IsSameQueue(string queueName, string? expectedQueueName)
    {
        if (string.IsNullOrWhiteSpace(queueName) || string.IsNullOrWhiteSpace(expectedQueueName))
            return false;

        return string.Equals(queueName, expectedQueueName, StringComparison.OrdinalIgnoreCase);
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

    private static void SetStatus(string message, StatusTone tone)
    {
        switch (tone)
        {
            case StatusTone.Error:
                AppLogger.Error("PRINT", message);
                break;
            case StatusTone.Warning:
                AppLogger.Warn("PRINT", message);
                break;
            default:
                AppLogger.Info("PRINT", message);
                break;
        }
    }

    private sealed class PrinterListItem
    {
        public PrinterListItem(PrinterCatalogItem printer)
        {
            QueueName = printer.QueueName;
            IsDefault = printer.IsDefault;
            DisplayName = printer.DisplayName;
            TypeText = printer.TypeText;
            LocationText = printer.LocationText;
            StatusText = printer.StatusText;
            IsOffline = printer.IsOffline;
        }

        public string QueueName { get; }
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
