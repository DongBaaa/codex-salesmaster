using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class InventoryWindow : Window
{
    private readonly EntityEditSessionMonitor? _editSessionMonitor;
    private bool _allowCloseWithoutSave;
    private bool _closeInProgress;

    public InventoryWindow(InventoryViewModel vm)
    {
        InitializeComponent();
        ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(this);
        DataContext = vm;
        Activated += InventoryWindow_Activated;
        Closing += Window_Closing;
        Loaded += (_, _) => _editSessionMonitor?.Start();
        vm.PropertyChanged += InventoryViewModel_PropertyChanged;
        Closed += (_, _) =>
        {
            vm.PropertyChanged -= InventoryViewModel_PropertyChanged;
            _editSessionMonitor?.Dispose();
            var cleanupTask = vm.CancelPendingBackgroundWorkAsync();
            UiTaskHelper.Forget(
                cleanupTask,
                "UI",
                "재고 창 종료 작업 정리",
                ex => AppLogger.Error("UI", "재고 창 종료 작업 정리 실패", ex));
        };

        _editSessionMonitor = EntityEditSessionMonitor.TryCreate(
            this,
            "품목/재고 관리",
            () =>
            {
                var selected = vm.SelectedItem;
                var editingItemId = selected?.Id ?? (!vm.IsNew ? vm.EditId : Guid.Empty);
                if (editingItemId == Guid.Empty)
                    return null;

                var displayName = selected?.NameOriginal ?? vm.EditName;
                return new EditSessionSubject(
                    "Item",
                    editingItemId.ToString("D"),
                    string.IsNullOrWhiteSpace(displayName) ? "품목" : displayName);
            });
    }

    private void InventoryViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(InventoryViewModel.SelectedItem), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(InventoryViewModel.IsNew), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(InventoryViewModel.EditId), StringComparison.Ordinal) ||
            string.Equals(e.PropertyName, nameof(InventoryViewModel.EditName), StringComparison.Ordinal))
            _editSessionMonitor?.RefreshSubject();
    }

    private void InventoryWindow_Activated(object? sender, EventArgs e)
        => UiTaskHelper.Run(
            this,
            async () =>
            {
                if (DataContext is not InventoryViewModel vm)
                    return;

                await vm.ReloadItemCategoryOptionsAsync();
            },
            "UI",
            "재고 창 활성화 처리",
            "재고 창을 갱신하는 중 오류가 발생했습니다.");

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12) { DialogWindowCloseHelper.Close(this); e.Handled = true; }
    }

    private void InventoryTransferButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(
            this,
            async () =>
            {
                if (DataContext is not InventoryViewModel vm)
                    return;

                var transferVm = new InventoryTransferViewModel(vm.LocalStateService, vm.SessionState);
                await transferVm.LoadAsync();

                var window = new InventoryTransferWindow(transferVm) { Owner = this };
                window.Closed += (_, _) => UiTaskHelper.Run(
                    this,
                    vm.LoadAsync,
                    "UI",
                    "재고이동 창 종료 후 재고 재조회",
                    "재고 목록을 다시 불러오는 중 오류가 발생했습니다.");
                WindowShowHelper.ShowModeless(window);
            },
            "UI",
            "재고이동 창 열기",
            "재고이동 창을 여는 중 오류가 발생했습니다.");

    private void DeleteItemButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(
            this,
            DeleteSelectedItemAsync,
            "UI",
            "품목 삭제",
            "품목을 삭제하는 중 오류가 발생했습니다.");

    private async Task DeleteSelectedItemAsync()
    {
        if (DataContext is not InventoryViewModel vm ||
            vm.SelectedItem is not { } selectedItem ||
            !vm.CanDeleteSelectedItem)
        {
            return;
        }

        var itemName = string.IsNullOrWhiteSpace(selectedItem.NameOriginal)
            ? "선택한 품목"
            : selectedItem.NameOriginal.Trim();
        var unsavedDraftWarning = vm.HasMeaningfulDraftContentForClose && vm.HasPendingChanges
            ? "현재 편집 중인 저장되지 않은 변경 내용도 함께 사라집니다.\n\n"
            : string.Empty;
        var confirmation = MessageBox.Show(
            this,
            $"{unsavedDraftWarning}'{itemName}' 품목을 휴지통으로 이동할까요?\n현재 재고 표시가 0으로 정리됩니다. 기존 전표와 재고이동 이력은 유지되며 휴지통에서 복원할 수 있습니다.",
            "품목 삭제 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
            return;

        await vm.DeleteItemCommand.ExecuteAsync(null);
    }

    private void ResetInventoryButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(
            this,
            async () =>
            {
                if (DataContext is not InventoryViewModel vm)
                    return;

                var selectedRows = GetSelectedInventoryRows(vm);
                if (selectedRows.Count == 0)
                {
                    MessageBox.Show(
                        this,
                        "재고를 초기화할 품목을 하나 이상 선택하세요.",
                        "재고 초기화",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var selectedItemLabel = BuildSelectedInventoryResetLabel(selectedRows);
                var confirmationMessage = vm.HasMeaningfulDraftContentForClose && vm.HasPendingChanges
                    ? $"현재 편집 중인 품목의 저장되지 않은 내용은 새로고침 과정에서 사라질 수 있습니다.\n\n{selectedItemLabel}의 재고를 0으로 초기화할까요?\n기존 전표/재고이동 이력은 유지되고 초기화 시점 이후 재고만 다시 계산합니다."
                    : $"{selectedItemLabel}의 재고를 0으로 초기화할까요?\n기존 전표/재고이동 이력은 유지되고 초기화 시점 이후 재고만 다시 계산합니다.";

                var confirmation = MessageBox.Show(
                    this,
                    confirmationMessage,
                    "재고 초기화",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirmation != MessageBoxResult.Yes)
                    return;

                var result = await vm.ResetSelectedInventoryValuesAsync(selectedRows);
                if (!result.Success)
                {
                    MessageBox.Show(
                        this,
                        result.Message,
                        "재고 초기화",
                        MessageBoxButton.OK,
                        result.PermissionDenied || result.NotFound ? MessageBoxImage.Warning : MessageBoxImage.Error);
                    return;
                }

                MessageBox.Show(
                    this,
                    result.Message,
                    "재고 초기화",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            },
            "UI",
            "재고 초기화",
            "재고 초기화 중 오류가 발생했습니다.");

    private IReadOnlyList<InventoryItemRow> GetSelectedInventoryRows(InventoryViewModel vm)
    {
        var rows = ItemsDataGrid.SelectedItems
            .OfType<InventoryItemRow>()
            .GroupBy(row => row.Id)
            .Select(group => group.First())
            .ToList();
        if (rows.Count == 0 && vm.SelectedItem is not null)
            rows.Add(vm.SelectedItem);

        return rows;
    }

    private static string BuildSelectedInventoryResetLabel(IReadOnlyList<InventoryItemRow> rows)
    {
        if (rows.Count == 1)
        {
            var itemName = string.IsNullOrWhiteSpace(rows[0].NameOriginal)
                ? "선택한 품목"
                : rows[0].NameOriginal;
            return $"'{itemName}' 품목";
        }

        var preview = string.Join(", ", rows
            .Select(row => string.IsNullOrWhiteSpace(row.NameOriginal) ? "이름 없는 품목" : row.NameOriginal)
            .Take(3));
        return rows.Count > 3
            ? $"선택한 {rows.Count:N0}개 품목({preview} 외)"
            : $"선택한 {rows.Count:N0}개 품목({preview})";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => DialogWindowCloseHelper.Close(this);

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (DataContext is not InventoryViewModel vm)
            return;

        try
        {
            if (_allowCloseWithoutSave)
                return;

            if (_closeInProgress)
            {
                e.Cancel = true;
                return;
            }

            if (!vm.HasMeaningfulDraftContentForClose || !vm.HasPendingChanges)
                return;

            e.Cancel = true;
            _closeInProgress = true;
            var requestDeferredClose = false;
            var previousCursor = Mouse.OverrideCursor;
            try
            {
                IsEnabled = false;
                Mouse.OverrideCursor = Cursors.Wait;

                var saved = await vm.TryAutoSaveOnCloseAsync();
                if (saved)
                {
                    _allowCloseWithoutSave = true;
                    requestDeferredClose = true;
                }
                else
                {
                    var discard = MessageBox.Show(
                        $"{vm.StatusMessage}\n\n저장되지 않은 변경사항이 있습니다. 저장 없이 닫을까요?",
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
            AppLogger.Error("UI", "재고 창 닫기 처리 실패", ex);
            e.Cancel = true;
            IsEnabled = true;
            Mouse.OverrideCursor = null;
            _closeInProgress = false;

            MessageBox.Show(
                this,
                $"재고 창을 닫는 중 오류가 발생했습니다.{Environment.NewLine}{ex.Message}",
                "오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
