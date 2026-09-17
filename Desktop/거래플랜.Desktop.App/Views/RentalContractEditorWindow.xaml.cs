using System.Windows;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class RentalContractEditorWindow : Window
{
    private const double CompactWorkspaceWidthThreshold = 1000d;

    private readonly RentalContractEditorViewModel _viewModel;
    private IDisposable? _authorizationMonitor;
    private bool _closeRequested;
    private bool? _isCompactWorkspaceLayout;
    private bool _showCompactEditorPane = true;
    private GridLength _normalEditorColumnWidth = new(42d, GridUnitType.Star);
    private GridLength _normalPreviewColumnWidth = new(58d, GridUnitType.Star);
    private double _normalEditorColumnMinWidth = 560d;

    public RentalContractEditorWindow(RentalContractEditorViewModel viewModel)
    {
        InitializeComponent();
        ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(this);
        _viewModel = viewModel;
        DataContext = viewModel;
        PreviewPrintCommandRouting.Bind(this, ContractDocumentViewer, _viewModel.PrintCommand);
        _viewModel.CurrentPageNumberProvider = () => ContractDocumentViewer.MasterPageNumber;
        _viewModel.RequestClose += OnRequestClose;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _authorizationMonitor ??= _viewModel.MonitorAuthorization();
        ApplyResponsiveWorkspaceLayout();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveWorkspaceLayout();
    }

    private void ApplyResponsiveWorkspaceLayout()
    {
        if (ActualWidth <= 0d)
            return;

        var useCompactLayout = ActualWidth < CompactWorkspaceWidthThreshold;
        if (_isCompactWorkspaceLayout == useCompactLayout)
            return;

        if (useCompactLayout)
        {
            _normalEditorColumnWidth = RentalContractEditorColumn.Width;
            _normalPreviewColumnWidth = RentalContractPreviewColumn.Width;
            _normalEditorColumnMinWidth = RentalContractEditorColumn.MinWidth;
            _showCompactEditorPane = true;
        }

        _isCompactWorkspaceLayout = useCompactLayout;
        RentalContractCompactPaneSwitcher.Visibility = useCompactLayout
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyCompactPaneVisibility();
    }

    private void ApplyCompactPaneVisibility()
    {
        var useCompactLayout = _isCompactWorkspaceLayout == true;
        if (!useCompactLayout)
        {
            RentalContractEditorColumn.Width = _normalEditorColumnWidth;
            RentalContractEditorColumn.MinWidth = _normalEditorColumnMinWidth;
            RentalContractSplitterColumn.Width = new GridLength(6d);
            RentalContractPreviewColumn.Width = _normalPreviewColumnWidth;
            RentalContractEditorScrollViewer.Visibility = Visibility.Visible;
            RentalContractWorkspaceSplitter.Visibility = Visibility.Visible;
            RentalContractPreviewBorder.Visibility = Visibility.Visible;
        }
        else
        {
            RentalContractEditorColumn.MinWidth = 0d;
            RentalContractEditorColumn.Width = _showCompactEditorPane
                ? new GridLength(1d, GridUnitType.Star)
                : new GridLength(0d);
            RentalContractSplitterColumn.Width = new GridLength(0d);
            RentalContractPreviewColumn.Width = _showCompactEditorPane
                ? new GridLength(0d)
                : new GridLength(1d, GridUnitType.Star);
            RentalContractEditorScrollViewer.Visibility = _showCompactEditorPane
                ? Visibility.Visible
                : Visibility.Collapsed;
            RentalContractWorkspaceSplitter.Visibility = Visibility.Collapsed;
            RentalContractPreviewBorder.Visibility = _showCompactEditorPane
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        ShowCompactContractEditorButton.IsEnabled =
            !useCompactLayout || !_showCompactEditorPane;
        ShowCompactContractPreviewButton.IsEnabled =
            !useCompactLayout || _showCompactEditorPane;
    }

    private void ShowCompactContractEditorButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _showCompactEditorPane = true;
        ApplyCompactPaneVisibility();
    }

    private void ShowCompactContractPreviewButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        _showCompactEditorPane = false;
        ApplyCompactPaneVisibility();
    }

    private async void OnRequestClose()
    {
        if (_closeRequested)
            return;
        _closeRequested = true;

        // Hide contract details immediately, including while a native save dialog is open.
        Content = null;
        await DialogWindowCloseHelper.WaitForNoActiveNativeDialogsAsync();
        if (!IsLoaded)
            return;

        foreach (Window child in OwnedWindows.Cast<Window>().ToArray())
            DialogWindowCloseHelper.Close(child, false);

        // Let an owned ShowDialog unwind before destroying its modeless owner.
        await Dispatcher.InvokeAsync(static () => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        if (IsLoaded)
            DialogWindowCloseHelper.Close(this, true);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _authorizationMonitor?.Dispose();
        _authorizationMonitor = null;
        _viewModel.CurrentPageNumberProvider = null;
        _viewModel.RequestClose -= OnRequestClose;
        Loaded -= OnLoaded;
        SizeChanged -= OnSizeChanged;
        Closed -= OnClosed;
    }
}
