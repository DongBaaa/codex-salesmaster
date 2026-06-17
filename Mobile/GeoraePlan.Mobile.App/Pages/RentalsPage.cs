using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;
using System.Text.Json;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class RentalsPage : ContentPage
{
    private readonly RentalsViewModel _viewModel;
    private readonly MobileRefreshCoordinator _refreshCoordinator;
    private int _seenRentalsVersion;

    public RentalsPage()
    {
        GeoraePlanTheme.ApplyPage(this, "렌탈 조회");

        _viewModel = ServiceHelper.GetRequiredService<RentalsViewModel>();
        _refreshCoordinator = ServiceHelper.GetRequiredService<MobileRefreshCoordinator>();
        _refreshCoordinator.AllChanged += HandleRealtimeRefreshRequested;
        BindingContext = _viewModel;

        var sectionGrid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            }
        };

        var profilesButton = GeoraePlanTheme.CreateCompactButton("청구프로필", GeoraePlanTheme.SecondaryButton);
        profilesButton.SetBinding(Button.BackgroundColorProperty, nameof(RentalsViewModel.ProfilesButtonColor));
        profilesButton.Clicked += (_, _) => _viewModel.ShowBillingProfiles();

        var assetsButton = GeoraePlanTheme.CreateCompactButton("렌탈자산", GeoraePlanTheme.SecondaryButton);
        assetsButton.SetBinding(Button.BackgroundColorProperty, nameof(RentalsViewModel.AssetsButtonColor));
        assetsButton.Clicked += (_, _) => _viewModel.ShowRentalAssets();

        var logsButton = GeoraePlanTheme.CreateCompactButton("청구 이력", GeoraePlanTheme.SecondaryButton);
        logsButton.SetBinding(Button.BackgroundColorProperty, nameof(RentalsViewModel.BillingLogsButtonColor));
        logsButton.Clicked += (_, _) => _viewModel.ShowBillingLogs();

        sectionGrid.Add(profilesButton);
        sectionGrid.Add(assetsButton, 1, 0);
        sectionGrid.Add(logsButton, 2, 0);

        var searchBar = GeoraePlanTheme.CreateSearchBar("거래처 / 장비 / 청구상태 검색");
        searchBar.SetBinding(SearchBar.TextProperty, nameof(RentalsViewModel.SearchText));
        searchBar.SearchButtonPressed += (_, _) =>
            MobileErrorHandler.FireAndForget(
                async () => await _viewModel.RefreshAsync(),
                "렌탈 작업");

        var refreshButton = GeoraePlanTheme.CreateButton("새로고침", GeoraePlanTheme.SecondaryButton);
        refreshButton.SetBinding(Button.CommandProperty, nameof(RentalsViewModel.RefreshCommand));

        var syncButton = GeoraePlanTheme.CreateButton("서버 동기화", GeoraePlanTheme.Accent);
        syncButton.SetBinding(Button.CommandProperty, nameof(RentalsViewModel.SyncNowCommand));

        var actionGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            ColumnSpacing = 8
        };
        actionGrid.Add(refreshButton);
        actionGrid.Add(syncButton, 1, 0);

        var titleLabel = GeoraePlanTheme.CreateSectionTitle(string.Empty, 15);
        titleLabel.SetBinding(Label.TextProperty, nameof(RentalsViewModel.CurrentSectionTitle));

        var summaryLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        summaryLabel.SetBinding(Label.TextProperty, nameof(RentalsViewModel.CurrentSectionSummary));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(RentalsViewModel.StatusMessage));

        var profileList = CreateProfilesView();
        profileList.SetBinding(ItemsView.ItemsSourceProperty, nameof(RentalsViewModel.BillingProfiles));
        profileList.SetBinding(VisualElement.IsVisibleProperty, nameof(RentalsViewModel.IsProfilesSection));
        profileList.SetBinding(VisualElement.HeightRequestProperty, nameof(RentalsViewModel.CurrentListHeight));

        var assetList = CreateAssetsView();
        assetList.SetBinding(ItemsView.ItemsSourceProperty, nameof(RentalsViewModel.RentalAssets));
        assetList.SetBinding(VisualElement.IsVisibleProperty, nameof(RentalsViewModel.IsAssetsSection));
        assetList.SetBinding(VisualElement.HeightRequestProperty, nameof(RentalsViewModel.CurrentListHeight));

        var billingLogList = CreateBillingLogsView();
        billingLogList.SetBinding(ItemsView.ItemsSourceProperty, nameof(RentalsViewModel.BillingLogs));
        billingLogList.SetBinding(VisualElement.IsVisibleProperty, nameof(RentalsViewModel.IsBillingLogsSection));
        billingLogList.SetBinding(VisualElement.HeightRequestProperty, nameof(RentalsViewModel.CurrentListHeight));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 12,
                Spacing = 12,
                Children =
                {
                    GeoraePlanTheme.CreateCompactCard(
                        GeoraePlanTheme.CreateSectionTitle("렌탈 조회", 15),
                        GeoraePlanTheme.CreateBodyText("같은 서버 sync 데이터 기준으로 청구프로필, 자산, 청구 이력을 조회합니다.", true, 12),
                        GeoraePlanTheme.CreateBodyText("모바일 렌탈은 조회 전용입니다. 청구 생성, 입금 등록, 프로필/자산 수정은 PC 렌탈 청구관리에서 처리하세요.", true, 12),
                        sectionGrid,
                        searchBar,
                        actionGrid,
                        titleLabel,
                        summaryLabel,
                        statusLabel),
                    profileList,
                    assetList,
                    billingLogList
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
if (_viewModel.NeedsRefresh(TimeSpan.FromSeconds(15)))
            await _viewModel.RefreshAsync();
        _seenRentalsVersion = _refreshCoordinator.RentalsVersion;
            },
            "렌탈 화면 초기화");
    }

    private void HandleRealtimeRefreshRequested(object? sender, EventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
            MobileErrorHandler.FireAndForget(
                async () =>
                {
                    if (Shell.Current?.CurrentPage == this && _seenRentalsVersion != _refreshCoordinator.RentalsVersion)
                    {
                        await _viewModel.RefreshAsync();
                        _seenRentalsVersion = _refreshCoordinator.RentalsVersion;
                    }
                },
                "렌탈 실시간 갱신"));
    }

    protected override bool OnBackButtonPressed()
    {
        if (_viewModel.TryNavigateBackOneStep())
            return true;

        return base.OnBackButtonPressed();
    }

    private static CollectionView CreateProfilesView()
    {
        return new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("동기화된 청구프로필이 없습니다.", true, 12),
            ItemTemplate = new DataTemplate(() =>
            {
                var title = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
                title.FontAttributes = FontAttributes.Bold;
                title.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new RentalProfileTitleConverter()));

                var subtitle = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
                subtitle.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new RentalProfileSubtitleConverter()));

                var meta = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                meta.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new RentalProfileMetaConverter()));

                var note = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                note.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new RentalProfileNoteConverter()));

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.SurfaceAlt,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Padding = 12,
                    Margin = new Thickness(0, 0, 0, 8),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { title, subtitle, meta, note }
                    }
                };
            })
        };
    }

    private static CollectionView CreateAssetsView()
    {
        return new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("동기화된 렌탈자산이 없습니다.", true, 12),
            ItemTemplate = new DataTemplate(() =>
            {
                var title = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
                title.FontAttributes = FontAttributes.Bold;
                title.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new RentalAssetTitleConverter()));

                var subtitle = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
                subtitle.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new RentalAssetSubtitleConverter()));

                var meta = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                meta.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new RentalAssetMetaConverter()));

                var note = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                note.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new RentalAssetNoteConverter()));

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.SurfaceAlt,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Padding = 12,
                    Margin = new Thickness(0, 0, 0, 8),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { title, subtitle, meta, note }
                    }
                };
            })
        };
    }

    private static CollectionView CreateBillingLogsView()
    {
        return new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("동기화된 청구 이력이 없습니다.", true, 12),
            ItemTemplate = new DataTemplate(() =>
            {
                var title = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
                title.FontAttributes = FontAttributes.Bold;
                title.SetBinding(Label.TextProperty, nameof(RentalBillingHistoryDisplayRow.Title));

                var subtitle = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
                subtitle.SetBinding(Label.TextProperty, nameof(RentalBillingHistoryDisplayRow.Subtitle));

                var meta = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                meta.SetBinding(Label.TextProperty, nameof(RentalBillingHistoryDisplayRow.Meta));

                var note = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                note.SetBinding(Label.TextProperty, nameof(RentalBillingHistoryDisplayRow.Note));

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.SurfaceAlt,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Padding = 12,
                    Margin = new Thickness(0, 0, 0, 8),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { title, subtitle, meta, note }
                    }
                };
            })
        };
    }

    private sealed class RentalProfileTitleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not RentalBillingProfileDto profile)
                return string.Empty;

            return string.IsNullOrWhiteSpace(profile.CustomerName)
                ? Normalize(profile.ProfileKey, "청구프로필")
                : $"{profile.CustomerName} · {Normalize(profile.ProfileKey, "프로필")}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class RentalProfileSubtitleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not RentalBillingProfileDto profile)
                return string.Empty;

            return $"{Normalize(profile.ItemName, "품명 미지정")} · 상태 {Normalize(profile.BillingStatus, "예정")} / 정산 {Normalize(profile.SettlementStatus, "미수")} · 월 {profile.MonthlyAmount:N0}원";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class RentalProfileMetaConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not RentalBillingProfileDto profile)
                return string.Empty;

            var billingDay = string.Equals(profile.BillingDayMode, RentalBillingScheduleRules.BillingDayModeEndOfMonth, StringComparison.Ordinal)
                ? "말일"
                : $"{profile.BillingDay}일";
            return $"주기 {Math.Max(1, profile.BillingCycleMonths)}개월 / 청구일 {billingDay} / 마지막청구 {FormatDate(profile.LastBilledDate)} / 지점 {ResolveOffice(profile.ResponsibleOfficeCode, profile.OfficeCode)}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class RentalProfileNoteConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not RentalBillingProfileDto profile)
                return string.Empty;

            var run = MobileRentalRunSnapshot.ResolveDisplayRun(profile.BillingRunsJson);
            if (run is not null)
            {
                var outstandingAmount = Math.Max(0m, run.BilledAmount - run.SettledAmount);
                return $"최근 회차 {Normalize(run.PeriodLabel, "기간 미정")} / 예정 {run.ScheduledDate:yyyy-MM-dd} / {Normalize(run.Status, "예정")} / 미수 {outstandingAmount:N0}원";
            }

            return string.IsNullOrWhiteSpace(profile.Notes)
                ? $"정산 {Normalize(profile.SettlementStatus, "미정")} / 미수 {profile.OutstandingAmount:N0}원"
                : profile.Notes.Trim();
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class RentalAssetTitleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not RentalAssetDto asset)
                return string.Empty;

            return string.IsNullOrWhiteSpace(asset.CustomerName)
                ? Normalize(asset.AssetKey, "렌탈자산")
                : $"{asset.CustomerName} · {Normalize(asset.ItemName, "품명 미지정")}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class RentalAssetSubtitleConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not RentalAssetDto asset)
                return string.Empty;

            return $"{Normalize(asset.AssetStatus, "상태 미지정")} · {Normalize(asset.CurrentLocation, "위치 미지정")}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class RentalAssetMetaConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not RentalAssetDto asset)
                return string.Empty;

            return $"월 {asset.MonthlyFee:N0}원 / 설치 {FormatDate(asset.InstallDate)} / 지점 {ResolveOffice(asset.ResponsibleOfficeCode, asset.OfficeCode)}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class RentalAssetNoteConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not RentalAssetDto asset)
                return string.Empty;

            return string.IsNullOrWhiteSpace(asset.Notes)
                ? Normalize(asset.InstallLocation, "설치 위치 미지정")
                : asset.Notes.Trim();
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string ResolveOffice(string? responsibleOfficeCode, string? ownerOfficeCode)
        => !string.IsNullOrWhiteSpace(responsibleOfficeCode)
            ? responsibleOfficeCode.Trim()
            : Normalize(ownerOfficeCode, "미지정");

    private static string FormatDate(DateOnly? value)
        => value.HasValue ? value.Value.ToString("yyyy-MM-dd") : "미정";

    private sealed class MobileRentalRunSnapshot
    {
        public Guid RunId { get; set; }
        public string RunKey { get; set; } = string.Empty;
        public DateOnly ScheduledDate { get; set; }
        public DateOnly PeriodStartDate { get; set; }
        public DateOnly PeriodEndDate { get; set; }
        public int CycleMonths { get; set; }
        public string PeriodLabel { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public decimal BilledAmount { get; set; }
        public decimal SettledAmount { get; set; }

        public static MobileRentalRunSnapshot? ResolveDisplayRun(string? billingRunsJson)
        {
            if (string.IsNullOrWhiteSpace(billingRunsJson))
                return null;

            try
            {
                var runs = JsonSerializer.Deserialize<List<MobileRentalRunSnapshot>>(billingRunsJson);
                return runs?
                    .Where(run => run.RunId != Guid.Empty)
                    .OrderByDescending(run => run.ScheduledDate)
                    .ThenByDescending(run => run.PeriodEndDate)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }
    }
}
