using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Views;

public partial class ServerIntegrityResolutionWindow : Window
{
    public ServerIntegrityResolutionWindow(ServerIntegrityResolutionPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        InitializeComponent();
        Plan = plan;
        DataContext = plan;
    }

    public ServerIntegrityResolutionPlan Plan { get; }
    public bool RecheckRequested { get; private set; }
    public DataIntegrityDirectActionKind RequestedAction { get; private set; }
    public Guid? RequestedEntityId { get; private set; }

    private void OpenTargetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Plan.CanOpenTarget)
            return;

        RequestedAction = Plan.DirectActionKind;
        RequestedEntityId = Plan.TargetEntityId;
        DialogResult = true;
    }

    private void RecheckButton_Click(object sender, RoutedEventArgs e)
    {
        RecheckRequested = true;
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => DialogWindowCloseHelper.Close(this);

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.F12 or Key.Escape))
            return;

        DialogWindowCloseHelper.Close(this);
        e.Handled = true;
    }
}
