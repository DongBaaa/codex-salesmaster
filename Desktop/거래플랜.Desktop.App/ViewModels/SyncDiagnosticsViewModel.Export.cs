using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class SyncDiagnosticsViewModel
{
    private readonly SyncDiagnosticExcelExportService _syncDiagnosticExcelExport = new();

    [RelayCommand]
    private async Task ExportDiagnosticExcelAsync()
    {
        if (IsBusy)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "동기화 진단 엑셀 저장",
            Filter = "Excel 통합문서|*.xlsx",
            AddExtension = true,
            DefaultExt = ".xlsx",
            FileName = $"sync-diagnostics-{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
            InitialDirectory = AppPaths.UserDownloadsDir
        };

        if (dialog.ShowDialog() != true)
            return;

        IsBusy = true;
        try
        {
            var summary = await _diagnostics.GetSummaryAsync();
            var filter = new SyncDiagnosticFilter(
                SearchText,
                SelectedCategory,
                SelectedStatus,
                SelectedSeverity,
                OnlyRecoverable);

            await _syncDiagnosticExcelExport.ExportAsync(Events.ToList(), summary, filter, dialog.FileName);
            SummaryStatusText = $"동기화 진단 엑셀을 저장했습니다: {dialog.FileName}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
