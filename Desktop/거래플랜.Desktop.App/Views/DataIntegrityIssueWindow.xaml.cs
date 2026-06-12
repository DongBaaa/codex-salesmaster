using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class DataIntegrityIssueWindow : Window
{
    private readonly DataIntegrityIssueViewModel _viewModel;

    public DataIntegrityIssueWindow(DataIntegrityIssueViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }

    public DataIntegrityIssueDetail? RequestedIssue { get; private set; }
    public event EventHandler<DataIntegrityIssueFixRequestedEventArgs>? FixRequested;
    public event EventHandler<DataIntegrityIssueMergeRequestedEventArgs>? MergeRequested;

    private void FixSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedIssue is null)
        {
            MessageBox.Show(this, "수정할 점검 항목을 먼저 선택하세요.", "운영 점검", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_viewModel.SelectedIssue.HasDirectAction)
        {
            MessageBox.Show(this, "이 항목은 원본 화면 바로가기를 지원하지 않습니다. 상세 내용을 기준으로 수동 확인하세요.", "운영 점검", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RequestedIssue = _viewModel.SelectedIssue;
        if (FixRequested is null)
            DialogResult = true;
        else
            FixRequested.Invoke(this, new DataIntegrityIssueFixRequestedEventArgs(RequestedIssue));
    }

    private void MergeSelectedButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedIssue is null)
        {
            MessageBox.Show(this, "병합할 점검 항목을 먼저 선택하세요.", "운영 점검", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!_viewModel.SelectedIssue.CanMergeDuplicates)
        {
            MessageBox.Show(this, "이 항목은 자동 병합 대상이 아닙니다. 판단 정보를 확인한 뒤 원본 화면에서 수동 정리하세요.", "운영 점검", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        RequestedIssue = _viewModel.SelectedIssue;
        if (MergeRequested is null)
        {
            MessageBox.Show(this, "현재 창에서는 병합 처리를 실행할 수 없습니다. 창을 다시 열어 시도하세요.", "운영 점검", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MergeRequested.Invoke(this, new DataIntegrityIssueMergeRequestedEventArgs(RequestedIssue));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => DialogWindowCloseHelper.Close(this);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12 || e.Key == Key.Escape)
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

public sealed class DataIntegrityIssueFixRequestedEventArgs : EventArgs
{
    public DataIntegrityIssueFixRequestedEventArgs(DataIntegrityIssueDetail issue)
    {
        Issue = issue;
    }

    public DataIntegrityIssueDetail Issue { get; }
}

public sealed class DataIntegrityIssueMergeRequestedEventArgs : EventArgs
{
    public DataIntegrityIssueMergeRequestedEventArgs(DataIntegrityIssueDetail issue)
    {
        Issue = issue;
    }

    public DataIntegrityIssueDetail Issue { get; }
}
