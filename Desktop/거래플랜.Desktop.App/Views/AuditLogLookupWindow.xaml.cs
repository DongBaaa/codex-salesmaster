using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class AuditLogLookupWindow : Window
{
    public AuditLogLookupWindow(AuditLogLookupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SearchTextBox.Focus();
        Keyboard.Focus(SearchTextBox);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.F12 or Key.Escape))
            return;

        Close();
        e.Handled = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => DialogWindowCloseHelper.Close(this);

    private void CopyBeforeJsonButton_Click(object sender, RoutedEventArgs e)
        => CopyJson(BeforeJsonTextBox.Text);

    private void CopyAfterJsonButton_Click(object sender, RoutedEventArgs e)
        => CopyJson(AfterJsonTextBox.Text);

    private void CopyJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || string.Equals(text, "(기록 없음)", StringComparison.Ordinal))
            return;

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UI", $"작업 이력 JSON 복사 실패: {ex.Message}");
            MessageBox.Show(
                this,
                "클립보드에 JSON을 복사하지 못했습니다. 잠시 후 다시 시도하세요.",
                "작업 이력 조회",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
