using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class UpdateRequiredPage : ContentPage
{
    private readonly Func<UpdateRequiredPage, Task> _installRequested;
    private readonly Func<UpdateRequiredPage, Task> _retryRequested;
    private readonly Label _messageLabel;
    private readonly Label _versionLabel;
    private readonly Label _statusLabel;
    private readonly Button _installButton;
    private readonly Button _retryButton;
    private readonly ActivityIndicator _activity;
    private int _actionRunning;

    internal UpdateRequiredPage(
        MobileCompatibilityGateOutcome outcome,
        Func<UpdateRequiredPage, Task> installRequested,
        Func<UpdateRequiredPage, Task> retryRequested)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        _installRequested = installRequested ??
            throw new ArgumentNullException(nameof(installRequested));
        _retryRequested = retryRequested ??
            throw new ArgumentNullException(nameof(retryRequested));

        GeoraePlanTheme.ApplyPage(this, "필수 업데이트");
        NavigationPage.SetHasNavigationBar(this, false);
        Shell.SetNavBarIsVisible(this, false);

        _messageLabel = GeoraePlanTheme.CreateBodyText(
            string.Empty,
            muted: false,
            fontSize: 15);
        _versionLabel = GeoraePlanTheme.CreateBodyText(string.Empty);
        _statusLabel = GeoraePlanTheme.CreateStatusLabel();
        _statusLabel.TextColor = GeoraePlanTheme.TextSecondary;

        _installButton = GeoraePlanTheme.CreateButton(
            "업데이트 설치",
            GeoraePlanTheme.Accent);
        _installButton.Clicked += async (_, _) =>
            await RunActionAsync(_installRequested);

        _retryButton = GeoraePlanTheme.CreateButton(
            "다시 확인",
            GeoraePlanTheme.SecondaryButton);
        _retryButton.Clicked += async (_, _) =>
            await RunActionAsync(_retryRequested);

        _activity = new ActivityIndicator
        {
            Color = GeoraePlanTheme.Accent,
            IsVisible = false,
            IsRunning = false,
            HorizontalOptions = LayoutOptions.Center
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24, 40),
                Spacing = 18,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new Label
                    {
                        Text = "앱 업데이트가 필요합니다",
                        TextColor = GeoraePlanTheme.TextPrimary,
                        FontSize = 26,
                        FontAttributes = FontAttributes.Bold,
                        HorizontalTextAlignment = TextAlignment.Center
                    },
                    new Label
                    {
                        Text = "서버와 안전하게 동기화하려면 지원되는 거래플랜 앱을 설치해야 합니다.",
                        TextColor = GeoraePlanTheme.TextSecondary,
                        FontSize = 14,
                        HorizontalTextAlignment = TextAlignment.Center
                    },
                    GeoraePlanTheme.CreateCard(
                        GeoraePlanTheme.CreateSectionTitle("업데이트 안내"),
                        _messageLabel,
                        _versionLabel,
                        _statusLabel),
                    _installButton,
                    _retryButton,
                    _activity,
                    new Label
                    {
                        Text = "이 화면에서는 업무 데이터 동기화가 실행되지 않습니다. 기기에 저장된 미전송 자료와 첨부파일은 그대로 보존됩니다.",
                        TextColor = GeoraePlanTheme.TextSecondary,
                        FontSize = 12,
                        HorizontalTextAlignment = TextAlignment.Center
                    }
                }
            }
        };

        UpdateOutcome(outcome);
    }

    internal void UpdateOutcome(MobileCompatibilityGateOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        var update = outcome.Update;
        _messageLabel.Text = string.IsNullOrWhiteSpace(update.Message)
            ? "서버 정책에 따라 최신 앱 설치가 필요합니다."
            : update.Message;

        var buildText = update.LatestBuild is > 0
            ? $" (빌드 {update.LatestBuild.Value})"
            : string.Empty;
        _versionLabel.Text =
            $"현재 {update.CurrentVersion} / 필요 {update.LatestVersion}{buildText}";
        _statusLabel.Text = outcome.NetworkUnavailable
            ? $"{outcome.StatusMessage}{Environment.NewLine}네트워크 연결을 확인한 뒤 다시 시도하세요."
            : outcome.StatusMessage;
    }

    internal void SetStatus(string message)
        => _statusLabel.Text = message?.Trim() ?? string.Empty;

    protected override bool OnBackButtonPressed()
        => true;

    private async Task RunActionAsync(
        Func<UpdateRequiredPage, Task> action)
    {
        if (Interlocked.Exchange(ref _actionRunning, 1) == 1)
            return;

        SetBusy(true);
        try
        {
            await action(this);
        }
        finally
        {
            SetBusy(false);
            Interlocked.Exchange(ref _actionRunning, 0);
        }
    }

    private void SetBusy(bool busy)
    {
        _installButton.IsEnabled = !busy;
        _retryButton.IsEnabled = !busy;
        _activity.IsVisible = busy;
        _activity.IsRunning = busy;
    }
}
