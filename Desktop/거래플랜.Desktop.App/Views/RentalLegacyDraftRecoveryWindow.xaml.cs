using System.Windows;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalLegacyDraftRecoveryWindow : Window
{
    public RentalLegacyDraftRecoveryWindow(RentalLegacyDraftPreview preview)
    {
        InitializeComponent();
        DataContext = preview;
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (OwnershipConfirmed.IsChecked == true)
            DialogWindowCloseHelper.Close(this, true);
    }
}
