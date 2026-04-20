using System.Windows;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Views;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class MainViewModel
{
    [RelayCommand]
    private async Task OpenSyncDiagnosticsAsync()
    {
        var diagnosticsViewModel = new SyncDiagnosticsViewModel(_diagnostics, _sync, _api, _local, _rental, _session);
        await diagnosticsViewModel.LoadAsync();

        var window = new SyncDiagnosticsWindow(diagnosticsViewModel)
        {
            Owner = Application.Current?.MainWindow
        };
        window.ShowDialog();
    }
}
