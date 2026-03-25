using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class InvoiceHistoryWindow : Window
{
    private readonly List<InvoiceHistorySelectionRow> _allRows;
    private readonly ObservableCollection<InvoiceHistorySelectionRow> _visibleRows = new();

    public IReadOnlyList<Guid> SelectedInvoiceIds =>
        _allRows
            .Where(row => row.IsSelected)
            .Select(row => row.InvoiceId)
            .ToList();

    public InvoiceHistoryWindow(IReadOnlyList<InvoiceHistorySelectionRow> rows)
    {
        InitializeComponent();
        _allRows = rows.ToList();
        HistoryGrid.ItemsSource = _visibleRows;
        ApplyFilter(string.Empty);
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        => ApplyFilter(SearchBox.Text);

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _visibleRows)
            row.IsSelected = true;
    }

    private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _allRows)
            row.IsSelected = false;
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        => ConfirmSelection();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogWindowCloseHelper.Close(this, false);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogWindowCloseHelper.Close(this, false);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ConfirmSelection();
            e.Handled = true;
        }
    }

    private void ApplyFilter(string text)
    {
        IEnumerable<InvoiceHistorySelectionRow> filteredRows = _allRows;
        if (!string.IsNullOrWhiteSpace(text))
        {
            var keyword = text.Trim();
            filteredRows = filteredRows.Where(row =>
                row.InvoiceNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.Summary.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.Memo.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                row.InvoiceDateDisplay.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        _visibleRows.Clear();
        foreach (var row in filteredRows)
            _visibleRows.Add(row);
    }

    private void ConfirmSelection()
    {
        if (SelectedInvoiceIds.Count == 0)
        {
            MessageBox.Show(
                "불러올 이전 기록을 선택하세요.",
                "알림",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        DialogWindowCloseHelper.Close(this, true);
    }
}
