using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalCustomerOnboardingWindow : Window
{
    private const double CompactLayoutWidthThreshold = 920d;
    private const double CompactContentHeightThreshold = 620d;

    private bool _allowClose;
    private bool _closeInProgress;
    private bool? _isCompactLayout;
    private bool? _isCompactContentLayout;

    public RentalCustomerOnboardingWindow(RentalCustomerOnboardingViewModel viewModel)
    {
        InitializeComponent();
        ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(this);
        DataContext = viewModel;
        viewModel.Completed += HandleCompleted;
        Closing += HandleClosing;
        Closed += (_, _) => viewModel.Completed -= HandleCompleted;
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (ActualWidth <= 0d || ActualHeight <= 0d)
            return;

        var useCompactLayout = ActualWidth < CompactLayoutWidthThreshold;
        if (_isCompactLayout != useCompactLayout)
        {
            _isCompactLayout = useCompactLayout;
            OnboardingStepSidebar.Visibility = useCompactLayout
                ? Visibility.Collapsed
                : Visibility.Visible;
            OnboardingSidebarColumn.Width = useCompactLayout
                ? new GridLength(0d)
                : new GridLength(250d);
            OnboardingSidebarGapColumn.Width = useCompactLayout
                ? new GridLength(0d)
                : new GridLength(10d);
        }

        var useCompactContentLayout =
            ActualHeight < CompactContentHeightThreshold;
        if (_isCompactContentLayout == useCompactContentLayout)
            return;

        _isCompactContentLayout = useCompactContentLayout;
        CandidateAssetSummaryPanel.Visibility = useCompactContentLayout
            ? Visibility.Collapsed
            : Visibility.Visible;
        TemplateGuidancePanel.Visibility = useCompactContentLayout
            ? Visibility.Collapsed
            : Visibility.Visible;
        TemplateSummaryBorder.Visibility = useCompactContentLayout
            ? Visibility.Collapsed
            : Visibility.Visible;
        TemplateSummaryGapRow.Height = useCompactContentLayout
            ? new GridLength(0d)
            : new GridLength(10d);
    }

    private void HandleCompleted(object? sender, EventArgs e)
    {
        _allowClose = true;
        DialogWindowCloseHelper.Close(this, true);
    }

    private async void HandleClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _closeInProgress || DataContext is not RentalCustomerOnboardingViewModel viewModel)
            return;

        e.Cancel = true;
        _closeInProgress = true;
        try
        {
            await viewModel.FlushAutoSaveAsync();
            _allowClose = true;
        }
        catch (Exception ex)
        {
            _closeInProgress = false;
            viewModel.StatusMessage = $"자동저장 후 창을 닫지 못했습니다. {ex.Message}";
            return;
        }

        _closeInProgress = false;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            if (!_allowClose || !IsLoaded)
                return;

            Close();
        }));
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogWindowCloseHelper.Close(this, false);
    }

    private void CustomerLookupButton_Click(object sender, RoutedEventArgs e)
    {
        UiTaskHelper.Run(this, OpenCustomerLookupAsync, "UI", "신규 렌탈 거래처 조회", "거래처 조회 중 오류가 발생했습니다.");
    }

    private async Task OpenCustomerLookupAsync()
    {
        if (DataContext is not RentalCustomerOnboardingViewModel viewModel)
            return;

        var dialog = new LookupWindow(
            "거래처 조회",
            await viewModel.BuildCustomerLookupRowsAsync(),
            "거래처 등록",
            async () =>
            {
                var customerVm = new CustomerEditViewModel(viewModel.LocalStateService, viewModel.SessionState);
                await customerVm.LoadAsync();
                var customerWindow = new CustomerEditWindow(customerVm) { Owner = this };
                DialogWindowCloseHelper.ShowDialog(customerWindow);
                return await viewModel.BuildCustomerLookupRowsAsync();
            })
        { Owner = this };

        if (DialogWindowCloseHelper.ShowDialog(dialog) == true && dialog.SelectedRow?.Tag is LocalCustomer customer)
            viewModel.ApplySelectedCustomer(customer);
    }
}
