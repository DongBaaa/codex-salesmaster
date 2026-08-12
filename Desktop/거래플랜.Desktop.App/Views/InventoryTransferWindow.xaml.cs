using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class InventoryTransferWindow : Window
{
    private const double CompactWorkspaceWidthThreshold = 1120d;
    private const double CompactDetailHeightThreshold = 760d;

    private readonly InventoryTransferViewModel _vm;
    private bool _allowCloseWithoutSave;
    private bool _closeInProgress;
    private bool? _isCompactWorkspaceLayout;
    private bool? _isCompactDetailLayout;
    private bool _showCompactTransferList;
    private CompactTransferSection _compactTransferSection = CompactTransferSection.Entry;
    private GridLength _normalTransferListWidth = new(450d);
    private GridLength _normalTransferWorkWidth = new(1d, GridUnitType.Star);

    public InventoryTransferWindow(InventoryTransferViewModel vm)
    {
        InitializeComponent();
        ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(this);
        _vm = vm;
        DataContext = vm;
        Closing += Window_Closing;
        Closed += (_, _) => _vm.Dispose();
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (!IsLoaded)
            return;

        ApplyWorkspaceWidthLayout();
        ApplyDetailHeightLayout();
    }

    private void ApplyWorkspaceWidthLayout()
    {
        var useCompactLayout = ActualWidth < CompactWorkspaceWidthThreshold;
        if (_isCompactWorkspaceLayout != useCompactLayout)
        {
            if (useCompactLayout && _isCompactWorkspaceLayout == false)
            {
                _normalTransferListWidth = InventoryTransferListColumn.Width;
                _normalTransferWorkWidth = InventoryTransferWorkColumn.Width;
            }

            _isCompactWorkspaceLayout = useCompactLayout;
            InventoryTransferCompactPaneSwitcher.Visibility = useCompactLayout
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (useCompactLayout)
        {
            InventoryTransferListColumn.MinWidth = 0d;
            InventoryTransferSplitterColumn.Width = new GridLength(0d);
            InventoryTransferWorkspaceSplitter.Visibility = Visibility.Collapsed;
            InventoryTransferListColumn.Width = _showCompactTransferList
                ? new GridLength(1d, GridUnitType.Star)
                : new GridLength(0d);
            InventoryTransferWorkColumn.Width = _showCompactTransferList
                ? new GridLength(0d)
                : new GridLength(1d, GridUnitType.Star);
            InventoryTransferListPanel.Visibility = _showCompactTransferList
                ? Visibility.Visible
                : Visibility.Collapsed;
            InventoryTransferWorkPanel.Visibility = _showCompactTransferList
                ? Visibility.Collapsed
                : Visibility.Visible;
            ShowCompactTransferListButton.IsEnabled = !_showCompactTransferList;
            ShowCompactTransferWorkButton.IsEnabled = _showCompactTransferList;
            return;
        }

        InventoryTransferListColumn.MinWidth = 360d;
        InventoryTransferListColumn.Width = _normalTransferListWidth;
        InventoryTransferSplitterColumn.Width = new GridLength(5d);
        InventoryTransferWorkColumn.Width = _normalTransferWorkWidth;
        InventoryTransferListPanel.Visibility = Visibility.Visible;
        InventoryTransferWorkPanel.Visibility = Visibility.Visible;
        InventoryTransferWorkspaceSplitter.Visibility = Visibility.Visible;
        ShowCompactTransferListButton.IsEnabled = true;
        ShowCompactTransferWorkButton.IsEnabled = true;
    }

    private void ApplyDetailHeightLayout()
    {
        var useCompactLayout = ActualHeight < CompactDetailHeightThreshold;
        if (_isCompactDetailLayout != useCompactLayout)
        {
            _isCompactDetailLayout = useCompactLayout;
            InventoryTransferDetailSectionSwitcher.Visibility = useCompactLayout
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (!useCompactLayout)
        {
            InventoryTransferBasicRow.Height = GridLength.Auto;
            InventoryTransferEntryRow.Height = GridLength.Auto;
            InventoryTransferLinesRow.Height = new GridLength(1d, GridUnitType.Star);
            InventoryTransferBasicPanel.Visibility = Visibility.Visible;
            InventoryTransferEntryPanel.Visibility = Visibility.Visible;
            InventoryTransferLinesPanel.Visibility = Visibility.Visible;
            InventoryTransferEntryPanel.Margin = new Thickness(0d, 8d, 0d, 0d);
            InventoryTransferLinesPanel.Margin = new Thickness(0d, 8d, 0d, 0d);
            SetCompactSectionButtonsEnabled();
            return;
        }

        var showBasic = _compactTransferSection == CompactTransferSection.Basic;
        var showEntry = _compactTransferSection == CompactTransferSection.Entry;
        var showLines = _compactTransferSection == CompactTransferSection.Lines;

        InventoryTransferBasicRow.Height = showBasic
            ? new GridLength(1d, GridUnitType.Star)
            : new GridLength(0d);
        InventoryTransferEntryRow.Height = showEntry
            ? new GridLength(1d, GridUnitType.Star)
            : new GridLength(0d);
        InventoryTransferLinesRow.Height = showLines
            ? new GridLength(1d, GridUnitType.Star)
            : new GridLength(0d);
        InventoryTransferBasicPanel.Visibility = showBasic
            ? Visibility.Visible
            : Visibility.Collapsed;
        InventoryTransferEntryPanel.Visibility = showEntry
            ? Visibility.Visible
            : Visibility.Collapsed;
        InventoryTransferLinesPanel.Visibility = showLines
            ? Visibility.Visible
            : Visibility.Collapsed;
        InventoryTransferEntryPanel.Margin = new Thickness(0d);
        InventoryTransferLinesPanel.Margin = new Thickness(0d);
        SetCompactSectionButtonsEnabled();
    }

    private void SetCompactSectionButtonsEnabled()
    {
        ShowCompactTransferBasicButton.IsEnabled =
            _compactTransferSection != CompactTransferSection.Basic;
        ShowCompactTransferEntryButton.IsEnabled =
            _compactTransferSection != CompactTransferSection.Entry;
        ShowCompactTransferLinesButton.IsEnabled =
            _compactTransferSection != CompactTransferSection.Lines;
    }

    private void ShowCompactTransferListButton_Click(object sender, RoutedEventArgs e)
    {
        _showCompactTransferList = true;
        ApplyResponsiveLayout();
    }

    private void ShowCompactTransferWorkButton_Click(object sender, RoutedEventArgs e)
    {
        _showCompactTransferList = false;
        ApplyResponsiveLayout();
    }

    private void ShowCompactTransferBasicButton_Click(object sender, RoutedEventArgs e)
        => ShowCompactTransferSection(CompactTransferSection.Basic);

    private void ShowCompactTransferEntryButton_Click(object sender, RoutedEventArgs e)
        => ShowCompactTransferSection(CompactTransferSection.Entry);

    private void ShowCompactTransferLinesButton_Click(object sender, RoutedEventArgs e)
        => ShowCompactTransferSection(CompactTransferSection.Lines);

    private void ShowCompactTransferSection(CompactTransferSection section)
    {
        _compactTransferSection = section;
        ApplyResponsiveLayout();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
        {
            DialogWindowCloseHelper.Close(this);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F6)
        {
            _vm.NewTransferCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F8)
        {
            _vm.SaveTransferCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogWindowCloseHelper.Close(this);

    private void DeleteTransferButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(
            this,
            async () =>
            {
                if (!_vm.CanDeleteTransfer)
                    return;

                var confirm = MessageBox.Show(
                    this,
                    $"재고이동 문서 '{_vm.TransferNumberDisplay}'를 삭제하시겠습니까?",
                    "재고이동 삭제",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);

                if (confirm != MessageBoxResult.Yes)
                    return;

                await _vm.DeleteCurrentTransferAsync();
            },
            "UI",
            "재고이동 문서 삭제",
            "재고이동 문서를 삭제하는 중 오류가 발생했습니다.");

    private void InputItemNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        e.Handled = true;
        var keyword = InputItemNameTextBox.Text.Trim();
        var matches = _vm.FindItemsForQuickInput(keyword);
        if (matches.Count == 0)
        {
            _vm.StatusMessage = "입력한 품명과 일치하는 상품이 없습니다.";
            return;
        }

        if (matches.Count == 1)
        {
            _vm.ApplyInputItem(matches[0]);
            _vm.StatusMessage = "이동 품목을 입력칸으로 불러왔습니다.";
            return;
        }

        ShowItemLookup(matches, "이동 품목 선택");
    }

    private void ItemLookupButton_Click(object sender, RoutedEventArgs e)
    {
        var keyword = InputItemNameTextBox.Text.Trim();
        var items = _vm.FindItemsForQuickInput(keyword);
        if (items.Count == 0)
            items = _vm.FindItemsForQuickInput(string.Empty);

        ShowItemLookup(items, "품목 목록");
    }

    private void ShowItemLookup(IReadOnlyList<LocalItem> items, string title)
    {
        var rows = items
            .Select(item => new LookupRow
            {
                Id = item.Id,
                PrimaryText = item.NameOriginal,
                SecondaryText = _vm.BuildItemLookupDescription(item),
                Tag = item
            })
            .ToList();

        var dialog = new LookupWindow(title, rows) { Owner = this };
        if (DialogWindowCloseHelper.ShowDialog(dialog) == true && dialog.SelectedRow?.Tag is LocalItem selected)
        {
            _vm.ApplyInputItem(selected);
            _vm.StatusMessage = "이동 품목을 입력칸으로 불러왔습니다.";
            InputItemNameTextBox.Focus();
            InputItemNameTextBox.SelectAll();
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        try
        {
            if (_allowCloseWithoutSave)
                return;

            if (_closeInProgress)
            {
                e.Cancel = true;
                return;
            }

            if (!_vm.HasMeaningfulDraftContentForClose || !_vm.HasPendingChanges)
                return;

            e.Cancel = true;
            _closeInProgress = true;
            var requestDeferredClose = false;
            var previousCursor = Mouse.OverrideCursor;
            try
            {
                IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;

                var saved = await _vm.TryAutoSaveOnCloseAsync();
                if (saved)
                {
                    _allowCloseWithoutSave = true;
                    requestDeferredClose = true;
                }
                else
                {
                    var discard = MessageBox.Show(
                        $"{_vm.StatusMessage}\n\n저장되지 않은 변경사항이 있습니다. 저장 없이 닫을까요?",
                        "확인",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (discard == MessageBoxResult.Yes)
                    {
                        _allowCloseWithoutSave = true;
                        requestDeferredClose = true;
                    }
                }
            }
            finally
            {
                Mouse.OverrideCursor = previousCursor;
                if (!_allowCloseWithoutSave)
                    IsEnabled = true;
                _closeInProgress = false;
            }

            if (requestDeferredClose)
                _ = Dispatcher.BeginInvoke(new Action(() => DialogWindowCloseHelper.Close(this)));
        }
        catch (Exception ex)
        {
            AppLogger.Error("UI", "재고이동 창 닫기 처리 실패", ex);
            e.Cancel = true;
            IsEnabled = true;
            Mouse.OverrideCursor = null;
            _closeInProgress = false;

            MessageBox.Show(
                this,
                $"재고이동 창을 닫는 중 오류가 발생했습니다.{Environment.NewLine}{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private enum CompactTransferSection
    {
        Basic,
        Entry,
        Lines
    }
}
