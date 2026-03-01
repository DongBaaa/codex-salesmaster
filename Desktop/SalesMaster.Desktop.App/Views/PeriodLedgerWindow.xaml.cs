using System.Windows;
using System.Windows.Input;
using SalesMaster.Desktop.App.ViewModels;

namespace SalesMaster.Desktop.App.Views;

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
        Close();
    }

    private void PeriodLedgerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
        {
            Close();
            e.Handled = true;
        }
    }
}
