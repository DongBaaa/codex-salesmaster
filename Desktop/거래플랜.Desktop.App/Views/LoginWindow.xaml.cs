using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class LoginWindow : Window
{
    private readonly LoginViewModel _vm;

    public LoginWindow(LoginViewModel vm)
    {
        InitializeComponent();
        Title = AppRuntimeInfo.WithTestLabel(Title);
        _vm = vm;
        DataContext = vm;
        vm.LoginSucceeded += () =>
        {
            DialogWindowCloseHelper.Close(this, true);
        };
        Loaded += (_, _) =>
        {
            PasswordBox.Password = _vm.Password;
            if (string.IsNullOrWhiteSpace(_vm.Username))
                UsernameBox.Focus();
            else
                PasswordBox.Focus();
        };
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.Password = PasswordBox.Password;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        _vm.Password = PasswordBox.Password;
        if (!_vm.LoginCommand.CanExecute(null))
            return;

        _vm.SubmitLogin();
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Password = PasswordBox.Password;
        base.OnClosed(e);
    }
}
