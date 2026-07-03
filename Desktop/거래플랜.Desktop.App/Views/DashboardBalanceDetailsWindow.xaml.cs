using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class DashboardBalanceDetailsWindow : Window
{
    public DashboardBalanceDetailsWindow(DashboardBalanceDetailViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12 || e.Key == Key.Escape)
        {
            DialogWindowCloseHelper.Close(this);
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => DialogWindowCloseHelper.Close(this);

    private void BalanceRowsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not null)
            return;

        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.DataContext is not DashboardBalanceDetailRow row)
            return;

        UiTaskHelper.Run(
            this,
            () => OpenInvoiceAsync(row),
            "UI",
            "잔액 상세 전표 열기",
            "전표를 여는 중 오류가 발생했습니다.");
    }

    private void BalanceRowsDataGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        var row = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row?.DataContext is not DashboardBalanceDetailRow balanceRow)
            return;

        grid.SelectedItem = balanceRow;
        if (DataContext is DashboardBalanceDetailViewModel viewModel)
            viewModel.SelectedRow = balanceRow;

        row.IsSelected = true;
        row.Focus();
    }

    private void OpenInvoiceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSelectedRow() is not { } row)
        {
            MessageBox.Show(this, "열 전표를 먼저 선택하세요.", "전표 열기", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UiTaskHelper.Run(
            this,
            () => OpenInvoiceAsync(row),
            "UI",
            "잔액 상세 전표 열기",
            "전표를 여는 중 오류가 발생했습니다.");
    }

    private void DeleteInvoiceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (ResolveSelectedRow() is not { } row)
        {
            MessageBox.Show(this, "삭제할 전표를 먼저 선택하세요.", "전표 삭제", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UiTaskHelper.Run(
            this,
            () => DeleteInvoiceAsync(row),
            "UI",
            "잔액 상세 전표 삭제",
            "전표를 삭제하는 중 오류가 발생했습니다.");
    }

    private async Task OpenInvoiceAsync(DashboardBalanceDetailRow row)
    {
        var mainWindow = ResolveMainWindow();
        if (mainWindow is null)
        {
            MessageBox.Show(this, "메인 화면을 찾을 수 없어 전표를 열 수 없습니다.", "전표 열기", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await mainWindow.OpenInvoiceFromChildWindowAsync(row.InvoiceId, this);
    }

    private async Task DeleteInvoiceAsync(DashboardBalanceDetailRow row)
    {
        var mainWindow = ResolveMainWindow();
        if (mainWindow is null)
        {
            MessageBox.Show(this, "메인 화면을 찾을 수 없어 전표를 삭제할 수 없습니다.", "전표 삭제", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var deleted = await mainWindow.DeleteInvoiceFromChildWindowAsync(row.InvoiceId, row.Revision, this);
        if (!deleted)
            return;

        if (DataContext is DashboardBalanceDetailViewModel viewModel)
            await viewModel.RefreshAsync();
    }

    private DashboardBalanceDetailRow? ResolveSelectedRow()
        => BalanceRowsDataGrid.SelectedItem as DashboardBalanceDetailRow
           ?? (DataContext as DashboardBalanceDetailViewModel)?.SelectedRow;

    private MainWindow? ResolveMainWindow()
        => Owner as MainWindow
           ?? Application.Current?.MainWindow as MainWindow
           ?? Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T found)
                return found;

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
