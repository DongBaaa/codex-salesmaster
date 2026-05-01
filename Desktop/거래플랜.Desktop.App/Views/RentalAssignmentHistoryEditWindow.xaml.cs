using System.Windows;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalAssignmentHistoryEditWindow : Window
{
    public RentalAssetAssignmentHistoryEditRequest EditRequest { get; }

    public RentalAssignmentHistoryEditWindow(RentalAssetAssignmentHistoryEditRequest editRequest)
    {
        EditRequest = editRequest ?? throw new ArgumentNullException(nameof(editRequest));
        InitializeComponent();
        DataContext = EditRequest;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (EditRequest.LinkedAtLocal == default)
        {
            MessageBox.Show("시작일을 입력하세요.", "임대이력", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!EditRequest.IsCurrent && EditRequest.UnlinkedAtLocal is null)
        {
            MessageBox.Show("종료된 임대이력은 회수/종료일을 입력하세요.", "임대이력", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
