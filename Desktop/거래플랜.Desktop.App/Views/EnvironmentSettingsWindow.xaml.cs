using System.Windows;
using System.ComponentModel;
using System.Windows.Input;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.ViewModels;

namespace 거래플랜.Desktop.App.Views;

public enum EnvironmentSettingsInitialTab
{
    General,
    RecycleBin,
    Sync
}

public partial class EnvironmentSettingsWindow : Window
{
    private readonly EnvironmentSettingsViewModel _viewModel;

    public EnvironmentSettingsWindow(EnvironmentSettingsViewModel vm, EnvironmentSettingsInitialTab initialTab = EnvironmentSettingsInitialTab.General)
    {
        InitializeComponent();
        _viewModel = vm;
        DataContext = vm;
        Closing += EnvironmentSettingsWindow_Closing;

        Loaded += (_, _) =>
        {
            SettingsTabs.SelectedItem = initialTab switch
            {
                EnvironmentSettingsInitialTab.RecycleBin => RecycleBinTab,
                EnvironmentSettingsInitialTab.Sync => SyncTab,
                _ => SettingsTabs.SelectedItem
            };
        };
    }

    private void EnvironmentSettingsWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (!_viewModel.IsBusy)
            return;

        e.Cancel = true;
        _viewModel.StatusMessage =
            "업체 DB 전환 또는 설정 작업이 진행 중입니다. 완료된 뒤 창을 닫아 주세요.";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogWindowCloseHelper.Close(this);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12)
        {
            DialogWindowCloseHelper.Close(this);
            e.Handled = true;
        }
    }
}
