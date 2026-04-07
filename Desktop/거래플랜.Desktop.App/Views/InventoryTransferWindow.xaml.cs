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
    private readonly InventoryTransferViewModel _vm;
    private bool _allowCloseWithoutSave;
    private bool _closeInProgress;

    public InventoryTransferWindow(InventoryTransferViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Closing += Window_Closing;
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
                    $"재고이동 문서 '{_vm.TransferNumberDisplay}'를 삭제하시겠습니까?",
                    "재고이동 삭제",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

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
        if (dialog.ShowDialog() == true && dialog.SelectedRow?.Tag is LocalItem selected)
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
}
