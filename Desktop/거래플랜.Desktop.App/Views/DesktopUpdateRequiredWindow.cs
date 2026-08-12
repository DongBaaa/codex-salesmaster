using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Views;

internal sealed class DesktopUpdateRequiredWindow
    : Window
{
    private readonly DesktopCompatibilityGateService _gate;
    private readonly DesktopAppUpdateService _updateService;
    private readonly TextBlock _status;
    private readonly Button _retryButton;
    private readonly Button _installButton;
    private DesktopCompatibilityGateDecision _decision;
    private bool _allowClose;
    private bool _busy;

    public DesktopUpdateRequiredWindow(
        DesktopCompatibilityGateService gate,
        DesktopAppUpdateService updateService,
        DesktopCompatibilityGateDecision decision)
    {
        _gate = gate;
        _updateService = updateService;
        _decision = decision;
        Title = "거래플랜 업데이트 필요";
        Width = 620;
        Height = 410;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation =
            WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        Topmost = true;

        var title = new TextBlock
        {
            Text = "필수 PC 업데이트가 필요합니다",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.DarkRed,
            Margin = new Thickness(0, 0, 0, 16)
        };
        var explanation = new TextBlock
        {
            Text =
                "현재 실행 중인 거래플랜은 서버 호환성 정책을 충족하지 않습니다." +
                Environment.NewLine +
                "로그인, 저장, 자동저장 및 동기화는 업데이트가 확인될 때까지 시작되지 않습니다." +
                Environment.NewLine +
                "로컬 변경 및 미동기화 내역은 삭제하지 않고 그대로 보존합니다.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            LineHeight = 23
        };
        _status = new TextBlock
        {
            Margin = new Thickness(0, 18, 0, 18),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray
        };
        _retryButton = CreateButton(
            "다시 확인",
            RetryAsync);
        _installButton = CreateButton(
            "검증된 업데이트 다운로드/설치",
            InstallAsync);
        var diagnosticsButton = CreateButton(
            "진단 폴더 열기",
            OpenDiagnosticsAsync);
        var exitButton = CreateButton(
            "종료",
            ExitAsync);
        var buttons = new WrapPanel
        {
            HorizontalAlignment =
                HorizontalAlignment.Right
        };
        buttons.Children.Add(_retryButton);
        buttons.Children.Add(_installButton);
        buttons.Children.Add(diagnosticsButton);
        buttons.Children.Add(exitButton);

        Content = new Border
        {
            Padding = new Thickness(28),
            Background = Brushes.White,
            Child = new StackPanel
            {
                Children =
                {
                    title,
                    explanation,
                    _status,
                    buttons
                }
            }
        };
        Closing += HandleClosing;
        RefreshDecisionText();
    }

    private static Button CreateButton(
        string text,
        Func<Task> action)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 104,
            MinHeight = 38,
            Margin = new Thickness(6, 4, 0, 4),
            Padding = new Thickness(12, 4, 12, 4)
        };
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.Message,
                    "업데이트 확인",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        };
        return button;
    }

    private async Task RetryAsync()
    {
        if (_busy)
            return;
        SetBusy(true);
        try
        {
            _decision = await _gate.CheckAsync(
                CancellationToken.None);
            if (!_decision.IsBlocked)
            {
                _allowClose = true;
                DialogResult = true;
                Close();
                return;
            }

            RefreshDecisionText();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task InstallAsync()
    {
        if (_busy ||
            _decision.VerifiedPackage is null)
        {
            return;
        }

        SetBusy(true);
        try
        {
            var progress =
                new Progress<DesktopUpdateDownloadProgress>(
                    current =>
                    {
                        _status.Text =
                            current.TotalBytes is > 0
                                ? $"업데이트 다운로드 중: {current.DownloadedBytes:N0} / {current.TotalBytes:N0} bytes"
                                : $"업데이트 다운로드 중: {current.DownloadedBytes:N0} bytes";
                    });
            var prepared =
                await _updateService
                    .PrepareUpdatePackageAsync(
                        _decision.VerifiedPackage,
                        progress,
                        CancellationToken.None);
            await _updateService.StartUpdateAsync(
                _decision.VerifiedPackage,
                prepared.PackagePath);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private Task OpenDiagnosticsAsync()
    {
        Directory.CreateDirectory(
            AppPaths.DiagnosticsDir);
        Process.Start(
            new ProcessStartInfo
            {
                FileName =
                    AppPaths.DiagnosticsDir,
                UseShellExecute = true
            });
        return Task.CompletedTask;
    }

    private Task ExitAsync()
    {
        _allowClose = true;
        DialogResult = false;
        Close();
        return Task.CompletedTask;
    }

    private void RefreshDecisionText()
    {
        _status.Text =
            _decision.VerifiedPackage is null
                ? $"복구 상태: {_decision.DiagnosticCode}. 검증된 stable PC 패키지를 아직 확인하지 못했습니다. 네트워크를 확인한 뒤 다시 시도하세요."
                : $"복구 상태: {_decision.DiagnosticCode}. 검증된 PC 업데이트 {_decision.VerifiedPackage.Version}을 설치할 수 있습니다.";
        _installButton.IsEnabled =
            _decision.VerifiedPackage is not null &&
            !_busy;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _retryButton.IsEnabled = !busy;
        _installButton.IsEnabled =
            !busy &&
            _decision.VerifiedPackage is not null;
    }

    private void HandleClosing(
        object? sender,
        CancelEventArgs args)
    {
        if (!_allowClose)
            args.Cancel = true;
    }
}
