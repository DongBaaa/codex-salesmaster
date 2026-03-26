using System.Windows;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public partial class InventoryWindow : Window
{
    public InventoryWindow(InventoryViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Activated += InventoryWindow_Activated;
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
}
