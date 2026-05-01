using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class PeriodLedgerWindow : Window
{
    public PeriodLedgerWindow(PeriodLedgerViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        PreviewKeyDown += PeriodLedgerWindow_PreviewKeyDown;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogWindowCloseHelper.Close(this);
    }

    private async void LedgerRowsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PeriodLedgerViewModel vm || vm.SelectedLedgerRow is null)
            return;

        var cell = FindVisualParent<DataGridCell>(e.OriginalSource as DependencyObject);
        if (!string.Equals(cell?.Column?.Header?.ToString(), "전표메모", StringComparison.Ordinal))
            return;

        if (!vm.SelectedLedgerRow.CanEditMemo)
        {
            MessageBox.Show("이 행은 수정 가능한 원본 메모와 연결되지 않았습니다.", "전표메모 수정", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var edited = ShowMemoEditDialog("전표메모 수정", "전표메모", vm.SelectedLedgerRow.Note);
        if (edited is null)
            return;

        var result = await vm.UpdateSelectedLedgerMemoAsync(edited);
        if (!result.Success)
            MessageBox.Show(result.Message, "전표메모 수정", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async void DetailItemsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not PeriodLedgerViewModel vm || vm.SelectedLedgerItem is null)
            return;

        var cell = FindVisualParent<DataGridCell>(e.OriginalSource as DependencyObject);
        if (!string.Equals(cell?.Column?.Header?.ToString(), "품목비고", StringComparison.Ordinal))
            return;

        var edited = ShowMemoEditDialog("품목비고 수정", "품목비고", vm.SelectedLedgerItem.ItemNote);
        if (edited is null)
            return;

        var result = await vm.UpdateSelectedItemMemoAsync(edited);
        if (!result.Success)
            MessageBox.Show(result.Message, "품목비고 수정", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static string? ShowMemoEditDialog(string title, string label, string currentValue)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 520,
            Height = 300,
            MinWidth = 440,
            MinHeight = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            Background = new SolidColorBrush(Color.FromRgb(21, 31, 46)),
            Owner = Application.Current.Windows.OfType<PeriodLedgerWindow>().FirstOrDefault(w => w.IsActive)
        };

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var caption = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Color.FromRgb(234, 242, 255)),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        };
        Grid.SetRow(caption, 0);
        root.Children.Add(caption);

        var input = new TextBox
        {
            Text = currentValue ?? string.Empty,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = new SolidColorBrush(Color.FromRgb(217, 230, 245)),
            Foreground = Brushes.Black,
            Padding = new Thickness(8),
            MinHeight = 120
        };
        Grid.SetRow(input, 1);
        root.Children.Add(input);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var okButton = new Button { Content = "저장", Width = 90, Height = 32, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancelButton = new Button { Content = "취소", Width = 90, Height = 32, IsCancel = true };
        okButton.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        dialog.Content = root;
        input.SelectAll();
        input.Focus();

        return dialog.ShowDialog() == true ? input.Text : null;
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T target)
                return target;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void PeriodLedgerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
        {
            DialogWindowCloseHelper.Close(this);
            e.Handled = true;
        }
    }
}
