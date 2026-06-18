using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class IntegrityReportPage : ContentPage
{
    private readonly IntegrityReportViewModel _viewModel;

    public IntegrityReportPage()
    {
        GeoraePlanTheme.ApplyPage(this, "운영점검");

        _viewModel = ServiceHelper.GetRequiredService<IntegrityReportViewModel>();
        BindingContext = _viewModel;

        var summaryLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
        summaryLabel.SetBinding(Label.TextProperty, nameof(IntegrityReportViewModel.SummaryText));

        var scopeLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        scopeLabel.SetBinding(Label.TextProperty, nameof(IntegrityReportViewModel.ScopeText));

        var generatedLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        generatedLabel.SetBinding(Label.TextProperty, nameof(IntegrityReportViewModel.GeneratedText));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(IntegrityReportViewModel.StatusMessage));

        var detailStatusLabel = GeoraePlanTheme.CreateStatusLabel();
        detailStatusLabel.SetBinding(Label.TextProperty, nameof(IntegrityReportViewModel.DetailStatusMessage));

        var activity = new ActivityIndicator { Color = GeoraePlanTheme.Accent };
        activity.SetBinding(ActivityIndicator.IsRunningProperty, nameof(IntegrityReportViewModel.IsBusy));
        activity.SetBinding(ActivityIndicator.IsVisibleProperty, nameof(IntegrityReportViewModel.IsBusy));

        var refreshButton = GeoraePlanTheme.CreateButton("운영점검 새로고침", GeoraePlanTheme.Accent);
        refreshButton.TextColor = Colors.Black;
        refreshButton.SetBinding(Button.CommandProperty, nameof(IntegrityReportViewModel.RefreshCommand));

        var clearDetailButton = GeoraePlanTheme.CreateCompactButton("상세 닫기", GeoraePlanTheme.SecondaryButton);
        clearDetailButton.SetBinding(Button.CommandProperty, nameof(IntegrityReportViewModel.ClearDetailCommand));

        var issueList = CreateIssueList();
        issueList.SetBinding(ItemsView.ItemsSourceProperty, nameof(IntegrityReportViewModel.Issues));
        issueList.SetBinding(VisualElement.HeightRequestProperty, nameof(IntegrityReportViewModel.IssueListHeight));

        var detailTitle = GeoraePlanTheme.CreateSectionTitle(string.Empty, 15);
        detailTitle.SetBinding(Label.TextProperty, nameof(IntegrityReportViewModel.SelectedIssueTitle));

        var detailSubtitle = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        detailSubtitle.SetBinding(Label.TextProperty, nameof(IntegrityReportViewModel.SelectedIssueSubtitle));

        var detailRows = CreateDetailList();
        detailRows.SetBinding(ItemsView.ItemsSourceProperty, nameof(IntegrityReportViewModel.DetailRows));
        detailRows.SetBinding(VisualElement.HeightRequestProperty, nameof(IntegrityReportViewModel.DetailListHeight));

        var detailCard = GeoraePlanTheme.CreateCompactCard(
            detailTitle,
            detailSubtitle,
            detailStatusLabel,
            clearDetailButton,
            detailRows);
        detailCard.SetBinding(VisualElement.IsVisibleProperty, nameof(IntegrityReportViewModel.HasSelectedIssue));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 12,
                Spacing = 12,
                Children =
                {
                    GeoraePlanTheme.CreateCompactCard(
                        GeoraePlanTheme.CreateSectionTitle("운영점검 / 무결성", 16),
                        GeoraePlanTheme.CreateBodyText("운영 서버의 전표·수금/지급·렌탈·첨부·품목/거래처 참조 무결성 결과를 읽기 전용으로 확인합니다.", true, 12),
                        GeoraePlanTheme.CreateBodyText("오류나 경고가 있으면 모바일에서 임의 수정하지 말고 PC 운영점검의 상세 조치 화면에서 처리하세요.", true, 12),
                        refreshButton,
                        summaryLabel,
                        scopeLabel,
                        generatedLabel,
                        activity,
                        statusLabel),
                    issueList,
                    detailCard
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        await MobileErrorHandler.RunGuardedAsync(
            async () => await _viewModel.RefreshAsync(),
            "운영점검 화면 초기화");
    }

    private CollectionView CreateIssueList()
    {
        return new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("표시할 무결성 이슈가 없습니다.", true, 12),
            ItemTemplate = new DataTemplate(() =>
            {
                var titleLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
                titleLabel.FontAttributes = FontAttributes.Bold;
                titleLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new IntegrityIssueTitleConverter()));

                var messageLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
                messageLabel.SetBinding(Label.TextProperty, nameof(IntegrityIssueDto.Message));

                var codeLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                codeLabel.SetBinding(Label.TextProperty, nameof(IntegrityIssueDto.Code));

                var badge = new BoxView
                {
                    WidthRequest = 5,
                    CornerRadius = 2,
                    HorizontalOptions = LayoutOptions.Start,
                    VerticalOptions = LayoutOptions.Fill
                };
                badge.SetBinding(BoxView.ColorProperty, new Binding(path: ".", converter: new IntegritySeverityColorConverter()));

                var textStack = new VerticalStackLayout
                {
                    Spacing = 4,
                    Children = { titleLabel, messageLabel, codeLabel }
                };

                var grid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(GridLength.Star)
                    },
                    ColumnSpacing = 10,
                    Children = { badge, textStack }
                };
                Grid.SetColumn(textStack, 1);

                var border = new Border
                {
                    BackgroundColor = GeoraePlanTheme.SurfaceAlt,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Padding = 12,
                    Margin = new Thickness(0, 0, 0, 8),
                    Content = grid
                };

                var tap = new TapGestureRecognizer();
                tap.Tapped += (sender, _) =>
                    MobileErrorHandler.FireAndForget(
                        async () =>
                        {
                            if (sender is Border card && card.BindingContext is IntegrityIssueDto issue)
                                await _viewModel.SelectIssueAsync(issue);
                        },
                        "운영점검 상세 조회");
                border.GestureRecognizers.Add(tap);
                return border;
            })
        };
    }

    private static CollectionView CreateDetailList()
    {
        return new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("상세 근거 행이 없습니다.", true, 11),
            ItemTemplate = new DataTemplate(() =>
            {
                var titleLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
                titleLabel.FontAttributes = FontAttributes.Bold;
                titleLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new IntegrityDetailTitleConverter()));

                var bodyLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                bodyLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new IntegrityDetailBodyConverter()));

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.Surface,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = new Thickness(10, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 3,
                        Children = { titleLabel, bodyLabel }
                    }
                };
            })
        };
    }

    private sealed class IntegrityIssueTitleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not IntegrityIssueDto issue)
                return string.Empty;

            return $"{NormalizeSeverity(issue.Severity)} · {issue.Count:N0}건";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class IntegritySeverityColorConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            var severity = value is IntegrityIssueDto issue ? NormalizeSeverity(issue.Severity) : "Info";
            return severity switch
            {
                "Error" => GeoraePlanTheme.Danger,
                "Warning" => GeoraePlanTheme.Brown,
                _ => GeoraePlanTheme.Accent
            };
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class IntegrityDetailTitleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not IntegrityIssueDetailRowDto row)
                return string.Empty;

            var primary = Normalize(row.PrimaryText, Normalize(row.EntityType, "상세"));
            var id = Normalize(row.EntityIdText, string.Empty);
            return string.IsNullOrWhiteSpace(id) ? primary : $"{primary} · {id}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class IntegrityDetailBodyConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not IntegrityIssueDetailRowDto row)
                return string.Empty;

            return string.Join(" / ", new[]
                {
                    row.SecondaryText,
                    row.ReferenceText,
                    row.ScopeText,
                    row.DetailText
                }
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim()));
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private static string NormalizeSeverity(string? severity)
        => string.IsNullOrWhiteSpace(severity) ? "Info" : severity.Trim();

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
