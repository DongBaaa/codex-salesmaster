using System.Linq;
using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class PaymentWindow : Window
{
    private const double CompactWorkspaceHeightThreshold = 620d;

    private readonly PaymentViewModel _vm;
    private bool? _isCompactWorkspaceLayout;
    private bool _showCompactCommandSection = true;
    private GridLength _normalCommandRowHeight = new(3d, GridUnitType.Star);
    private GridLength _normalWorkspaceRowHeight = new(2d, GridUnitType.Star);
    private double _normalCommandRowMinHeight = 100d;
    private double _normalCommandRowMaxHeight = 440d;
    private double _normalWorkspaceRowMinHeight = 112d;
    private double _normalWorkspaceRowMaxHeight = double.PositiveInfinity;

    public PaymentWindow(PaymentViewModel vm)
    {
        InitializeComponent();
        ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(this);
        _vm = vm;
        DataContext = vm;
        Loaded += (_, _) => ApplyResponsiveWorkspaceLayout();
        SizeChanged += (_, _) => ApplyResponsiveWorkspaceLayout();
    }

    private void ApplyResponsiveWorkspaceLayout()
    {
        if (ActualHeight <= 0d)
            return;

        var useCompactLayout = ActualHeight < CompactWorkspaceHeightThreshold;
        if (_isCompactWorkspaceLayout == useCompactLayout)
            return;

        if (useCompactLayout)
        {
            _normalCommandRowHeight = PaymentCommandRow.Height;
            _normalWorkspaceRowHeight = PaymentWorkspaceRow.Height;
            _normalCommandRowMinHeight = PaymentCommandRow.MinHeight;
            _normalCommandRowMaxHeight = PaymentCommandRow.MaxHeight;
            _normalWorkspaceRowMinHeight = PaymentWorkspaceRow.MinHeight;
            _normalWorkspaceRowMaxHeight = PaymentWorkspaceRow.MaxHeight;
            _showCompactCommandSection = true;
        }

        _isCompactWorkspaceLayout = useCompactLayout;
        PaymentCompactSectionSwitcher.Visibility = useCompactLayout
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyCompactSectionVisibility();
    }

    private void ApplyCompactSectionVisibility()
    {
        var useCompactLayout = _isCompactWorkspaceLayout == true;
        if (!useCompactLayout)
        {
            PaymentCommandRow.Height = _normalCommandRowHeight;
            PaymentCommandRow.MinHeight = _normalCommandRowMinHeight;
            PaymentCommandRow.MaxHeight = _normalCommandRowMaxHeight;
            PaymentWorkspaceRow.Height = _normalWorkspaceRowHeight;
            PaymentWorkspaceRow.MinHeight = _normalWorkspaceRowMinHeight;
            PaymentWorkspaceRow.MaxHeight = _normalWorkspaceRowMaxHeight;
            PaymentCommandScrollViewer.Visibility = Visibility.Visible;
            PaymentWorkspaceTabs.Visibility = Visibility.Visible;
            PaymentCommandWorkspaceSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            PaymentCommandRow.MinHeight = 0d;
            PaymentCommandRow.MaxHeight = double.PositiveInfinity;
            PaymentWorkspaceRow.MinHeight = 0d;
            PaymentWorkspaceRow.MaxHeight = double.PositiveInfinity;
            PaymentCommandRow.Height = _showCompactCommandSection
                ? new GridLength(1d, GridUnitType.Star)
                : new GridLength(0d);
            PaymentWorkspaceRow.Height = _showCompactCommandSection
                ? new GridLength(0d)
                : new GridLength(1d, GridUnitType.Star);
            PaymentCommandScrollViewer.Visibility = _showCompactCommandSection
                ? Visibility.Visible
                : Visibility.Collapsed;
            PaymentWorkspaceTabs.Visibility = _showCompactCommandSection
                ? Visibility.Collapsed
                : Visibility.Visible;
            PaymentCommandWorkspaceSplitter.Visibility = Visibility.Collapsed;
        }

        ShowCompactPaymentCommandButton.IsEnabled =
            !useCompactLayout || !_showCompactCommandSection;
        ShowCompactPaymentWorkspaceButton.IsEnabled =
            !useCompactLayout || _showCompactCommandSection;
    }

    private void ShowCompactPaymentCommandButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _showCompactCommandSection = true;
        ApplyCompactSectionVisibility();
    }

    private void ShowCompactPaymentWorkspaceButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _showCompactCommandSection = false;
        ApplyCompactSectionVisibility();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
        {
            DialogWindowCloseHelper.Close(this);
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogWindowCloseHelper.Close(this);

    private void HistoryDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
            return;

        if (!_vm.EditHistoryCommand.CanExecute(null))
            return;

        _vm.EditHistoryCommand.Execute(null);
        e.Handled = true;
    }

    private void CustomerSelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanSelectCustomer)
            return;

        var dlg = new LookupWindow(
            "거래처 검색",
            BuildCustomerRows(),
            "거래처 등록",
            async () =>
            {
                var customerVm = new CustomerEditViewModel(_vm.LocalStateService, _vm.SessionState);
                await customerVm.LoadAsync();
                var customerWindow = new CustomerEditWindow(customerVm) { Owner = this };
                DialogWindowCloseHelper.ShowDialog(customerWindow);

                await _vm.ReloadCustomersAsync();
                return BuildCustomerRows();
            })
        { Owner = this };

        if (DialogWindowCloseHelper.ShowDialog(dlg) == true && dlg.SelectedRow?.Tag is LocalCustomer selected)
        {
            _vm.SetCustomer(selected);
        }
    }

    private List<LookupRow> BuildCustomerRows()
    {
        return _vm.GetAllCustomers()
            .Select(c => new LookupRow
            {
                Id = c.Id,
                PrimaryText = c.NameOriginal,
                SecondaryText = c.Phone,
                Tag = c
            })
            .ToList();
    }
}
