using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Documents;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class PrintPreviewViewModel : ObservableObject
{
    private readonly IPrintService _printService;
    private readonly string _jobName;

    public event Action? RequestClose;

    [ObservableProperty] private IDocumentPaginatorSource _document;
    [ObservableProperty] private string _statusMessage = "미리보기를 확인한 뒤 인쇄를 진행하세요.";
    [ObservableProperty] private double _zoom = 100d;
    [ObservableProperty] private bool _wasPrinted;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    [NotifyCanExecuteChangedFor(nameof(CloseCommand))]
    private bool _isPrinting;

    public PrintPreviewViewModel(IDocumentPaginatorSource document, IPrintService printService, string jobName)
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

    private bool CanPrint() => !IsPrinting;

    private bool CanClose() => !IsPrinting;

    [RelayCommand(CanExecute = nameof(CanPrint))]
    private void Print()
    {
        if (IsPrinting)
            return;

        IsPrinting = true;
        StatusMessage = "프린터 선택 창을 여는 중...";

        try
        {
            if (_printService.TryPrint(Document, _jobName, out var errorMessage))
            {
                WasPrinted = true;
                StatusMessage = "인쇄를 완료했습니다.";
                RequestClose?.Invoke();
                return;
            }

            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                StatusMessage = "인쇄를 취소했습니다.";
                return;
            }

            StatusMessage = errorMessage;
            System.Windows.MessageBox.Show(
                errorMessage,
                "인쇄 오류",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            IsPrinting = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanClose))]
    private void Close()
    {
        RequestClose?.Invoke();
    }
}
