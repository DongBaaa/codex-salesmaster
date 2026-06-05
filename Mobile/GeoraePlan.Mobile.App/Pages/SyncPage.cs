using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class SyncPage : ContentPage
{
    private readonly SyncViewModel _viewModel;

    public SyncPage()
    {
        GeoraePlanTheme.ApplyPage(this, "동기화");

        _viewModel = ServiceHelper.GetRequiredService<SyncViewModel>();
        BindingContext = _viewModel;

        var revisionLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false);
        revisionLabel.SetBinding(Label.TextProperty, nameof(SyncViewModel.LastRevisionText), stringFormat: "마지막 서버 변경번호: {0}");

        var pendingLabel = GeoraePlanTheme.CreateBodyText(string.Empty);
        pendingLabel.SetBinding(Label.TextProperty, nameof(SyncViewModel.PendingText));

        var summaryLabel = GeoraePlanTheme.CreateBodyText(string.Empty);
        summaryLabel.SetBinding(Label.TextProperty, nameof(SyncViewModel.LastPullSummary));

        var autoSyncLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        autoSyncLabel.SetBinding(Label.TextProperty, nameof(SyncViewModel.AutoSyncText));

        var attentionLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
        attentionLabel.TextColor = Colors.White;
        attentionLabel.SetBinding(Label.TextProperty, nameof(SyncViewModel.AttentionText));

        var attentionCard = new Border
        {
            BackgroundColor = Color.FromArgb("#3A2B12"),
            Stroke = GeoraePlanTheme.Brown,
            StrokeShape = new RoundRectangle { CornerRadius = 14 },
            Padding = 14,
            Content = attentionLabel
        };
        attentionCard.SetBinding(IsVisibleProperty, nameof(SyncViewModel.HasAttention));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(SyncViewModel.StatusMessage));

        var activity = new ActivityIndicator { Color = GeoraePlanTheme.Accent };
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(SyncViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(SyncViewModel.IsBusy));

        var refreshButton = GeoraePlanTheme.CreateButton("상태 새로고침", GeoraePlanTheme.SecondaryButton);
        refreshButton.SetBinding(Button.CommandProperty, nameof(SyncViewModel.RefreshCommand));

        var syncNowButton = GeoraePlanTheme.CreateButton("권장 동기화 실행", GeoraePlanTheme.Accent);
        syncNowButton.SetBinding(Button.CommandProperty, nameof(SyncViewModel.SyncNowCommand));

        var pullButton = GeoraePlanTheme.CreateButton("서버에서 받기", GeoraePlanTheme.Success);
        pullButton.SetBinding(Button.CommandProperty, nameof(SyncViewModel.PullCommand));

        var pushButton = GeoraePlanTheme.CreateButton("서버에 올리기", GeoraePlanTheme.Purple);
        pushButton.SetBinding(Button.CommandProperty, nameof(SyncViewModel.PushCommand));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 12,
                Children =
                {
                    GeoraePlanTheme.CreateCard(
                        GeoraePlanTheme.CreateSectionTitle("동기화 상태"),
                        revisionLabel,
                        pendingLabel,
                        summaryLabel,
                        autoSyncLabel,
                        attentionCard,
                        GeoraePlanTheme.CreateBodyText("기본 권장: 저장 시 자동 서버 반영 + 첨부 업로드 + 최신 데이터 받기. 문제가 있을 때만 수동 버튼을 사용하세요.", true, 12),
                        syncNowButton,
                        refreshButton,
                        pullButton,
                        pushButton,
                        activity,
                        statusLabel)
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        await MobileErrorHandler.RunGuardedAsync(
            async () =>
            {
await _viewModel.RefreshAsync();
            },
            "동기화 화면 초기화");
    }
}
