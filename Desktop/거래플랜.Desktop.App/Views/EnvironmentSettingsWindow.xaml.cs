using System.Windows;
using System.Windows.Controls;
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
    private const double UpdateSingleColumnWidthThreshold = 900d;
    private readonly EnvironmentSettingsViewModel _viewModel;

    public EnvironmentSettingsWindow(EnvironmentSettingsViewModel vm, EnvironmentSettingsInitialTab initialTab = EnvironmentSettingsInitialTab.General)
    {
        InitializeComponent();
        _viewModel = vm;
        DataContext = vm;
        Closing += EnvironmentSettingsWindow_Closing;
        UpdateTabScrollViewer.SizeChanged += (_, _) => ApplyResponsiveUpdateLayout();

        Loaded += (_, _) =>
        {
            SettingsTabs.SelectedItem = initialTab switch
            {
                EnvironmentSettingsInitialTab.RecycleBin => RecycleBinTab,
                EnvironmentSettingsInitialTab.Sync => SyncTab,
                _ => SettingsTabs.SelectedItem
            };
            ApplyResponsiveUpdateLayout();
        };
    }

    private void ApplyResponsiveUpdateLayout()
    {
        var availableWidth = UpdateTabScrollViewer.ViewportWidth;
        if (!double.IsFinite(availableWidth) || availableWidth <= 0d)
            availableWidth = SettingsTabs.ActualWidth;
        if (!double.IsFinite(availableWidth) || availableWidth <= 0d)
            return;

        var useSingleColumn = availableWidth < UpdateSingleColumnWidthThreshold;
        if (useSingleColumn)
        {
            UpdateInstallStatusColumn.Width = new GridLength(1d, GridUnitType.Star);
            UpdateOverviewSpacerColumn.Width = new GridLength(0d);
            UpdateReleaseNotesColumn.Width = new GridLength(0d);
            Grid.SetRow(UpdateReleaseNotesPanel, 1);
            Grid.SetColumn(UpdateReleaseNotesPanel, 0);
            UpdateReleaseNotesPanel.Margin = new Thickness(0d, 12d, 0d, 0d);
            return;
        }

        UpdateInstallStatusColumn.Width = new GridLength(1.2d, GridUnitType.Star);
        UpdateOverviewSpacerColumn.Width = new GridLength(12d);
        UpdateReleaseNotesColumn.Width = new GridLength(1.8d, GridUnitType.Star);
        Grid.SetRow(UpdateReleaseNotesPanel, 0);
        Grid.SetColumn(UpdateReleaseNotesPanel, 2);
        UpdateReleaseNotesPanel.Margin = new Thickness(0d);
    }

    private void EnvironmentSettingsWindow_Closing(
        object? sender,
        CancelEventArgs e)
    {
        if (!_viewModel.IsCloseBlocked)
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
