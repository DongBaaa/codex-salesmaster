using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class InventoryTransfersPage : ContentPage
{
    private readonly InventoryTransfersViewModel _viewModel;
    private readonly SyncCoordinator _syncCoordinator;

    public InventoryTransfersPage()
    {
        GeoraePlanTheme.ApplyPage(this, "재고이동");

        _viewModel = ServiceHelper.GetRequiredService<InventoryTransfersViewModel>();
        _syncCoordinator = ServiceHelper.GetRequiredService<SyncCoordinator>();
        BindingContext = _viewModel;

        var searchBar = GeoraePlanTheme.CreateSearchBar("이동번호 / 메모 / 창고 검색");
        searchBar.SetBinding(SearchBar.TextProperty, nameof(InventoryTransfersViewModel.SearchText));
        searchBar.SearchButtonPressed += async (_, _) => await _viewModel.RefreshAsync();

        var refreshButton = GeoraePlanTheme.CreateButton("새로고침", GeoraePlanTheme.SecondaryButton);
        refreshButton.SetBinding(Button.CommandProperty, nameof(InventoryTransfersViewModel.RefreshCommand));

        var syncButton = GeoraePlanTheme.CreateButton("서버 동기화", GeoraePlanTheme.Accent);
        syncButton.SetBinding(Button.CommandProperty, nameof(InventoryTransfersViewModel.SyncNowCommand));

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

        var summaryLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        summaryLabel.SetBinding(Label.TextProperty, nameof(InventoryTransfersViewModel.SummaryText));

        var statusLabel = GeoraePlanTheme.CreateStatusLabel();
        statusLabel.SetBinding(Label.TextProperty, nameof(InventoryTransfersViewModel.StatusMessage));

        var transfersView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("동기화된 재고이동 내역이 없습니다.", true, 12),
            ItemTemplate = new DataTemplate(() =>
            {
                var titleLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 13);
                titleLabel.FontAttributes = FontAttributes.Bold;
                titleLabel.SetBinding(Label.TextProperty, nameof(InventoryTransferDto.TransferNumber));

                var routeLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
                routeLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new InventoryTransferRouteConverter()));

                var metaLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                metaLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new InventoryTransferMetaConverter()));

                var memoLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                memoLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new InventoryTransferMemoConverter()));

                var border = new Border
                {
                    BackgroundColor = GeoraePlanTheme.SurfaceAlt,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 12 },
                    Padding = 12,
                    Margin = new Thickness(0, 0, 0, 8),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 4,
                        Children = { titleLabel, routeLabel, metaLabel, memoLabel }
                    }
                };

                var tap = new TapGestureRecognizer();
                tap.Tapped += async (sender, _) =>
                {
                    if (sender is Border card && card.BindingContext is InventoryTransferDto transfer)
                        await _viewModel.SelectTransferAsync(transfer);
                };
                border.GestureRecognizers.Add(tap);
                return border;
            })
        };
        transfersView.SetBinding(ItemsView.ItemsSourceProperty, nameof(InventoryTransfersViewModel.Transfers));
        transfersView.SetBinding(VisualElement.HeightRequestProperty, nameof(InventoryTransfersViewModel.TransferListHeight));

        var detailTitle = GeoraePlanTheme.CreateSectionTitle(string.Empty, 15);
        detailTitle.SetBinding(Label.TextProperty, nameof(InventoryTransfersViewModel.SelectedTransferTitle));

        var detailRoute = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
        detailRoute.SetBinding(Label.TextProperty, nameof(InventoryTransfersViewModel.SelectedTransferRoute));

        var detailMeta = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        detailMeta.SetBinding(Label.TextProperty, nameof(InventoryTransfersViewModel.SelectedTransferMeta));

        var requestLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        requestLabel.SetBinding(Label.TextProperty, nameof(InventoryTransfersViewModel.SelectedTransferRequestText));

        var receiveLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        receiveLabel.SetBinding(Label.TextProperty, nameof(InventoryTransfersViewModel.SelectedTransferReceiveText));

        var rejectLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        rejectLabel.SetBinding(Label.TextProperty, nameof(InventoryTransfersViewModel.SelectedTransferRejectText));

        var memoTitle = GeoraePlanTheme.CreateFieldLabel("메모");
        var memoBody = GeoraePlanTheme.CreateBodyText(string.Empty, true, 12);
        memoBody.SetBinding(Label.TextProperty, nameof(InventoryTransfersViewModel.SelectedTransferMemo));

        var linesTitle = GeoraePlanTheme.CreateFieldLabel("이동 품목");

        var linesView = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            BackgroundColor = Colors.Transparent,
            EmptyView = GeoraePlanTheme.CreateBodyText("라인 정보가 없습니다.", true, 11),
            ItemTemplate = new DataTemplate(() =>
            {
                var itemLabel = GeoraePlanTheme.CreateBodyText(string.Empty, muted: false, fontSize: 12);
                itemLabel.SetBinding(Label.TextProperty, nameof(InventoryTransferLineDto.ItemNameOriginal));

                var specLabel = GeoraePlanTheme.CreateBodyText(string.Empty, true, 11);
                specLabel.SetBinding(Label.TextProperty, new Binding(path: ".", converter: new InventoryTransferLineMetaConverter()));

                return new Border
                {
                    BackgroundColor = GeoraePlanTheme.Surface,
                    Stroke = GeoraePlanTheme.Border,
                    StrokeShape = new RoundRectangle { CornerRadius = 10 },
                    Padding = new Thickness(10, 8),
                    Margin = new Thickness(0, 0, 0, 6),
                    Content = new VerticalStackLayout
                    {
                        Spacing = 2,
                        Children = { itemLabel, specLabel }
                    }
                };
            })
        };
        linesView.SetBinding(ItemsView.ItemsSourceProperty, nameof(InventoryTransfersViewModel.SelectedTransferLines));
        linesView.SetBinding(VisualElement.HeightRequestProperty, nameof(InventoryTransfersViewModel.SelectedTransferLinesHeight));

        var detailCard = GeoraePlanTheme.CreateCompactCard(
            detailTitle,
            detailRoute,
            detailMeta,
            requestLabel,
            receiveLabel,
            rejectLabel,
            memoTitle,
            memoBody,
            linesTitle,
            linesView);
        detailCard.SetBinding(VisualElement.IsVisibleProperty, nameof(InventoryTransfersViewModel.HasSelectedTransfer));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 12,
                Spacing = 12,
                Children =
                {
                    GeoraePlanTheme.CreateCompactCard(
                        GeoraePlanTheme.CreateSectionTitle("재고이동", 15),
                        GeoraePlanTheme.CreateBodyText("같은 서버 sync 데이터 기준으로 이동요청/수령 상태를 조회합니다.", true, 12),
                        searchBar,
                        actionGrid,
                        summaryLabel,
                        statusLabel),
                    transfersView,
                    detailCard
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _syncCoordinator.RefreshIfServerChangedAsync("inventory-transfers-page", TimeSpan.FromSeconds(5));
        if (_viewModel.NeedsRefresh(TimeSpan.FromSeconds(15)))
            await _viewModel.RefreshAsync();
    }

    private sealed class InventoryTransferRouteConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not InventoryTransferDto transfer)
                return string.Empty;

            return $"{WarehouseDisplayNameResolver.Resolve(transfer.FromWarehouseCode)} → {WarehouseDisplayNameResolver.Resolve(transfer.ToWarehouseCode)}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class InventoryTransferMetaConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not InventoryTransferDto transfer)
                return string.Empty;

            return $"{transfer.TransferDate:yyyy-MM-dd} · {Normalize(transfer.TransferStatus, "수령대기")} · 품목 {transfer.Lines?.Count ?? 0:N0}건";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class InventoryTransferMemoConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not InventoryTransferDto transfer)
                return string.Empty;

            if (!string.IsNullOrWhiteSpace(transfer.Memo))
                return transfer.Memo.Trim();

            if (!string.IsNullOrWhiteSpace(transfer.ReceiveMemo))
                return $"수령메모: {transfer.ReceiveMemo.Trim()}";

            return string.IsNullOrWhiteSpace(transfer.RejectReason)
                ? "메모 없음"
                : $"반려사유: {transfer.RejectReason.Trim()}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class InventoryTransferLineMetaConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not InventoryTransferLineDto line)
                return string.Empty;

            var specification = string.IsNullOrWhiteSpace(line.SpecificationOriginal) ? "규격 없음" : line.SpecificationOriginal.Trim();
            var remark = string.IsNullOrWhiteSpace(line.Remark) ? string.Empty : $" / {line.Remark.Trim()}";
            return $"{specification} / 수량 {line.Quantity:N0} {Normalize(line.Unit, "개")}{remark}";
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
            => throw new NotSupportedException();
    }

    private static string Normalize(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
