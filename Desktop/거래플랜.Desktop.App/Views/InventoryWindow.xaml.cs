using System.ComponentModel;
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
        DataContext = vm;
        Activated += InventoryWindow_Activated;
        Closing += Window_Closing;
        Loaded += (_, _) => _editSessionMonitor?.Start();
        Closed += (_, _) => _editSessionMonitor?.Dispose();

        _editSessionMonitor = EntityEditSessionMonitor.TryCreate(
            this,
            "품목/재고 관리",
            () =>
            {
                var selected = vm.SelectedItem;
                return selected is null
                    ? null
                    : new EditSessionSubject(
                        "Item",
                        selected.Id.ToString("D"),
                        string.IsNullOrWhiteSpace(selected.NameOriginal) ? "품목" : selected.NameOriginal);
            });
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
                window.Show();
                window.Activate();
            },
            "UI",
            "재고이동 창 열기",
            "재고이동 창을 여는 중 오류가 발생했습니다.");

    private void ResetInventoryButton_Click(object sender, RoutedEventArgs e)
        => UiTaskHelper.Run(
            this,
            async () =>
            {
                if (DataContext is not InventoryViewModel vm)
                    return;

                if (vm.SelectedItem is null)
                {
                    MessageBox.Show(
                        this,
                        "재고를 초기화할 품목을 먼저 선택하세요.",
                        "재고 초기화",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                var selectedItemName = string.IsNullOrWhiteSpace(vm.SelectedItem.NameOriginal)
                    ? "선택한 품목"
                    : vm.SelectedItem.NameOriginal;
                var confirmationMessage = vm.HasMeaningfulDraftContentForClose && vm.HasPendingChanges
                    ? $"현재 편집 중인 품목의 저장되지 않은 내용은 새로고침 과정에서 사라질 수 있습니다.\n\n'{selectedItemName}' 품목의 재고를 0으로 초기화할까요?\n기존 전표/재고이동 이력은 유지되고 초기화 시점 이후 재고만 다시 계산됩니다."
                    : $"'{selectedItemName}' 품목의 재고를 0으로 초기화할까요?\n기존 전표/재고이동 이력은 유지되고 초기화 시점 이후 재고만 다시 계산됩니다.";

                var confirmation = MessageBox.Show(
                    this,
                    confirmationMessage,
                    "재고 초기화",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirmation != MessageBoxResult.Yes)
                    return;

                var result = await vm.ResetSelectedInventoryValueAsync();
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
