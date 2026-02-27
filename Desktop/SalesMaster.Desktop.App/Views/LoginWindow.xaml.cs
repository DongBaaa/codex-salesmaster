using System.Windows;
using System.Windows.Input;
using SalesMaster.Desktop.App.ViewModels;

namespace SalesMaster.Desktop.App.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;

    public LoginWindow(LoginViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.LoginSucceeded += () =>
        {
            DialogResult = true;
            Close();
        };
        Loaded += (_, _) => UsernameBox.Focus();
    }

    private void PasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _vm.Password = PasswordBox.Password;
            if (_vm.LoginCommand.CanExecute(null))
                _vm.LoginCommand.Execute(null);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Password = PasswordBox.Password;
        base.OnClosed(e);
    }
}
