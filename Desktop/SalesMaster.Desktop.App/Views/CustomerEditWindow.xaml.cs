using System.Windows;
using System.Windows.Input;
using SalesMaster.Desktop.App.ViewModels;

namespace SalesMaster.Desktop.App.Views;

public partial class CustomerEditWindow : Window
{
    private readonly CustomerEditViewModel _vm;

    public CustomerEditWindow(CustomerEditViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.SavedAndClose += () =>
        {
            DialogResult = true;
            Close();
        };
        vm.SavedAndNew += () =>
        {
            // 저장 후 폼 초기화 완료 — 창은 유지
        };
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12) { Close(); e.Handled = true; }
        if (e.Key == Key.F6 && _vm.SaveAndNewCommand.CanExecute(null))
        {
            _vm.SaveAndNewCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
