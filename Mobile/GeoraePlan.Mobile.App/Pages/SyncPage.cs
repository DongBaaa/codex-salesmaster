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
        revisionLabel.SetBinding(Label.TextProperty, nameof(SyncViewModel.LastRevisionText), stringFormat: "마지막 Revision: {0}");

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

        var syncNowButton = GeoraePlanTheme.CreateButton("동기화", GeoraePlanTheme.Accent);
        syncNowButton.SetBinding(Button.CommandProperty, nameof(SyncViewModel.SyncNowCommand));

        var pullButton = GeoraePlanTheme.CreateButton("다운로드만", GeoraePlanTheme.Success);
        pullButton.SetBinding(Button.CommandProperty, nameof(SyncViewModel.PullCommand));

        var pushButton = GeoraePlanTheme.CreateButton("업로드만", GeoraePlanTheme.Purple);
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
                        GeoraePlanTheme.CreateBodyText("기본 추천: 저장 후 즉시 서버 반영 + 첨부 업로드 + 최신 데이터 pull", true, 12),
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
