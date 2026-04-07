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
    private bool _allowCloseWithoutSave;
    private bool _closeInProgress;

    public InventoryWindow(InventoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Activated += InventoryWindow_Activated;
        Closing += Window_Closing;
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
