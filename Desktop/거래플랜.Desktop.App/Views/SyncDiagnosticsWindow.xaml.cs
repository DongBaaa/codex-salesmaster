using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class SyncDiagnosticsWindow : Window
{
    private readonly SyncDiagnosticsViewModel _viewModel;

    public SyncDiagnosticsWindow(SyncDiagnosticsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    public event EventHandler<ServerIntegrityResolutionRequestedEventArgs>? ResolutionTargetRequested;

    private async void ServerIntegrityDetailDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid ||
            ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) is not DataGridRow ||
            _viewModel.SelectedServerIntegrityIssue is null ||
            _viewModel.SelectedServerIntegrityDetailRow is null)
        {
            return;
        }

        e.Handled = true;
        var plan = ServerIntegrityResolutionPlan.Create(
            _viewModel.SelectedServerIntegrityIssue,
            _viewModel.SelectedServerIntegrityDetailRow);
        var resolutionWindow = new ServerIntegrityResolutionWindow(plan)
        {
            Owner = this
        };

        if (DialogWindowCloseHelper.ShowDialog(resolutionWindow) != true)
            return;

        if (resolutionWindow.RecheckRequested)
        {
            await _viewModel.RecheckServerIntegrityIssueAsync(plan.Issue.Code);
            return;
        }

        if (resolutionWindow.RequestedAction == DataIntegrityDirectActionKind.None ||
            !resolutionWindow.RequestedEntityId.HasValue)
        {
            return;
        }

        if (ResolutionTargetRequested is null)
        {
            MessageBox.Show(
                this,
                "연결할 원본 화면을 확인하지 못했습니다. 해결 방법을 참고해 직접 확인해 주세요.",
                "무결성 문제 해결",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        ResolutionTargetRequested.Invoke(
            this,
            new ServerIntegrityResolutionRequestedEventArgs(
                resolutionWindow.RequestedAction,
                resolutionWindow.RequestedEntityId.Value,
                plan.Issue.Code));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => DialogWindowCloseHelper.Close(this);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
        {
            DialogWindowCloseHelper.Close(this);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F5 && _viewModel.RefreshCommand.CanExecute(null))
        {
            _viewModel.RefreshCommand.Execute(null);
            e.Handled = true;
        }
    }
}

public sealed class ServerIntegrityResolutionRequestedEventArgs : EventArgs
{
    public ServerIntegrityResolutionRequestedEventArgs(
        DataIntegrityDirectActionKind actionKind,
        Guid targetEntityId,
        string issueCode)
    {
        ActionKind = actionKind;
        TargetEntityId = targetEntityId;
        IssueCode = issueCode;
    }

    public DataIntegrityDirectActionKind ActionKind { get; }
    public Guid TargetEntityId { get; }
    public string IssueCode { get; }
}
