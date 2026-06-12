using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Views;

public enum DataIntegrityAlertAction
{
    None,
    Details,
    Fix
}

public partial class DataIntegrityAlertWindow : Window
{
    public DataIntegrityAlertWindow()
    {
        InitializeComponent();
    }

    public event EventHandler<DataIntegrityAlertActionRequestedEventArgs>? NonClosingActionRequested;

    public DataIntegrityAlertAction RequestedAction { get; private set; }
    public DataIntegrityIssueSummary? RequestedSummary { get; private set; }

    private void FixButton_Click(object sender, RoutedEventArgs e)
    {
        var summary = (sender as FrameworkElement)?.DataContext as DataIntegrityIssueSummary;
        RequestedAction = DataIntegrityAlertAction.Fix;
        RequestedSummary = summary;
        if (NonClosingActionRequested is null)
            Complete(DataIntegrityAlertAction.Fix, summary);
        else
            NonClosingActionRequested.Invoke(this, new DataIntegrityAlertActionRequestedEventArgs(DataIntegrityAlertAction.Fix, summary));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => DialogWindowCloseHelper.Close(this);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12 || e.Key == Key.Escape)
        {
            DialogWindowCloseHelper.Close(this);
            e.Handled = true;
        }
    }

    private void Complete(DataIntegrityAlertAction action, DataIntegrityIssueSummary? summary)
    {
        RequestedAction = action;
        RequestedSummary = summary;
        DialogResult = true;
    }
}

public sealed class DataIntegrityAlertActionRequestedEventArgs : EventArgs
{
    public DataIntegrityAlertActionRequestedEventArgs(DataIntegrityAlertAction action, DataIntegrityIssueSummary? summary)
    {
        Action = action;
        Summary = summary;
    }

    public DataIntegrityAlertAction Action { get; }
    public DataIntegrityIssueSummary? Summary { get; }
}
