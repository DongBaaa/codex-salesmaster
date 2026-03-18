using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Documents;
using SalesMaster.Desktop.App.Services;

namespace SalesMaster.Desktop.App.ViewModels;

public sealed partial class PrintPreviewViewModel : ObservableObject
{
    private readonly IPrintService _printService;
    private readonly string _jobName;

    public event Action? RequestClose;

    [ObservableProperty] private FixedDocument _document;
    [ObservableProperty] private string _statusMessage = "미리보기를 확인한 뒤 인쇄를 진행하세요.";
    [ObservableProperty] private double _zoom = 100d;
    [ObservableProperty] private bool _wasPrinted;

    public PrintPreviewViewModel(FixedDocument document, IPrintService printService, string jobName)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        _printService = printService ?? throw new ArgumentNullException(nameof(printService));
        _jobName = string.IsNullOrWhiteSpace(jobName) ? "거래명세서" : jobName;
    }

    [RelayCommand]
    private void ZoomIn()
    {
        Zoom = Math.Min(500d, Zoom + 10d);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        Zoom = Math.Max(20d, Zoom - 10d);
    }

    [RelayCommand]
    private void Print()
    {
        if (_printService.TryPrint(Document, _jobName, out var errorMessage))
        {
            WasPrinted = true;
            StatusMessage = "인쇄를 완료했습니다.";
            RequestClose?.Invoke();
            return;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            StatusMessage = errorMessage;
            System.Windows.MessageBox.Show(
                errorMessage,
                "인쇄 오류",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke();
    }
}
