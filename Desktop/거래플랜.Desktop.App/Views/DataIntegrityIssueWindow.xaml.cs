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
    }

    public DataIntegrityIssueDetail? RequestedIssue { get; private set; }

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
        DialogResult = true;
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
