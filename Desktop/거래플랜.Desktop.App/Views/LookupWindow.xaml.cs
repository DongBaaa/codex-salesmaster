using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;

namespace 거래플랜.Desktop.App.Views;

public partial class LookupWindow : Window
{
    private List<LookupRow> _allRows;
    private readonly Func<Task<IReadOnlyList<LookupRow>>>? _registerAndReloadAsync;

    public LookupRow? SelectedRow { get; private set; }

    public LookupWindow(
        string title,
        IReadOnlyList<LookupRow> rows,
        string? registerButtonText = null,
        Func<Task<IReadOnlyList<LookupRow>>>? registerAndReloadAsync = null)
    {
        InitializeComponent();
        Title = title;
        _allRows = rows.ToList();
        _registerAndReloadAsync = registerAndReloadAsync;

        ResultGrid.ItemsSource = _allRows;

        if (!string.IsNullOrWhiteSpace(registerButtonText) && _registerAndReloadAsync is not null)
        {
            RegisterButton.Content = registerButtonText;
            RegisterButton.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) => SearchBox.Focus();
    }

    private void ApplyFilter(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            ResultGrid.ItemsSource = _allRows;
            return;
        }

        ResultGrid.ItemsSource = _allRows
            .Where(r => r.PrimaryText.Contains(text, StringComparison.CurrentCultureIgnoreCase)
                     || r.SecondaryText.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => ApplyFilter(SearchBox.Text);

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && ResultGrid.Items.Count > 0)
        {
            ResultGrid.SelectedIndex = 0;
            ResultGrid.Focus();
        }
        else if (e.Key == Key.Enter)
        {
            SearchButton_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            DialogWindowCloseHelper.Close(this, false);
        }
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
        => ApplyFilter(SearchBox.Text);

    private void ResultGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => ConfirmSelection();

    private void ResultGrid_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ConfirmSelection();
        }
        else if (e.Key == Key.Escape)
        {
            DialogWindowCloseHelper.Close(this, false);
        }
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
        => ConfirmSelection();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogWindowCloseHelper.Close(this, false);
    }

    private async void RegisterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_registerAndReloadAsync is null)
        {
            return;
        }

        RegisterButton.IsEnabled = false;

        try
        {
            var refreshedRows = await _registerAndReloadAsync();
            _allRows = refreshedRows.ToList();
            ApplyFilter(SearchBox.Text);

            if (ResultGrid.Items.Count > 0)
            {
                ResultGrid.SelectedIndex = 0;
            }

            SearchBox.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"등록 창을 여는 중 오류가 발생했습니다.\n{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            RegisterButton.IsEnabled = true;
        }
    }

    private void ConfirmSelection()
    {
        SelectedRow = ResultGrid.SelectedItem as LookupRow;
        if (SelectedRow is null)
        {
            return;
        }

        DialogWindowCloseHelper.Close(this, true);
    }
}
