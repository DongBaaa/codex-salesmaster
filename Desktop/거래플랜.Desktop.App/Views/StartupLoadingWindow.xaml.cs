using System.ComponentModel;
using System.Windows;
using 거래플랜.Desktop.App.Infrastructure;

namespace 거래플랜.Desktop.App.Views;

public partial class StartupLoadingWindow : Window
{
    private bool _allowClose;

    public StartupLoadingWindow()
    {
        InitializeComponent();
        Title = AppRuntimeInfo.WithTestLabel(Title);
    }

    public void Complete()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
