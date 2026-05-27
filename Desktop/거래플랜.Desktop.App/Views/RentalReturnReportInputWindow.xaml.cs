using System.Windows;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalReturnReportInputWindow : Window
{
    public RentalReturnReportInputWindow(string? defaultReturnReason = null)
    {
        InitializeComponent();
        ReturnReasonBox.Text = defaultReturnReason ?? string.Empty;
        ReturnReasonBox.Focus();
        ReturnReasonBox.SelectAll();
    }

    public RentalReturnReportFields ReportFields { get; private set; } = new(string.Empty, string.Empty);

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        ReportFields = new RentalReturnReportFields(
            (ReturnReasonBox.Text ?? string.Empty).Trim(),
            (FaultDescriptionBox.Text ?? string.Empty).Trim());
        DialogResult = true;
    }
}
