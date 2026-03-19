using GeoraePlan.Mobile.App.ViewModels;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class SyncPage : ContentPage
{
    private readonly SyncViewModel _viewModel;

    public SyncPage()
    {
        Title = "동기화";
        _viewModel = ServiceHelper.GetRequiredService<SyncViewModel>();
        BindingContext = _viewModel;

        var revisionLabel = new Label();
        revisionLabel.SetBinding(Label.TextProperty, nameof(SyncViewModel.LastRevisionText), stringFormat: "마지막 Revision: {0}");

        var pendingLabel = new Label();
        pendingLabel.SetBinding(Label.TextProperty, nameof(SyncViewModel.PendingText));

        var summaryLabel = new Label();
        summaryLabel.SetBinding(Label.TextProperty, nameof(SyncViewModel.LastPullSummary));

        var statusLabel = new Label { TextColor = Colors.DimGray };
        statusLabel.SetBinding(Label.TextProperty, nameof(SyncViewModel.StatusMessage));

        var activity = new ActivityIndicator();
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(SyncViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(SyncViewModel.IsBusy));

        var refreshButton = new Button { Text = "상태 새로고침" };
        refreshButton.SetBinding(Button.CommandProperty, nameof(SyncViewModel.RefreshCommand));

        var pullButton = new Button { Text = "다운로드 동기화" };
        pullButton.SetBinding(Button.CommandProperty, nameof(SyncViewModel.PullCommand));

        var pushButton = new Button { Text = "업로드 동기화" };
        pushButton.SetBinding(Button.CommandProperty, nameof(SyncViewModel.PushCommand));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 24,
                Spacing = 12,
                Children =
                {
                    revisionLabel,
                    pendingLabel,
                    summaryLabel,
                    refreshButton,
                    pullButton,
                    pushButton,
                    activity,
                    statusLabel
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _viewModel.RefreshAsync();
    }
}
