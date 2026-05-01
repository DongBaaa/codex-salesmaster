using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace 거래플랜.Desktop.App.Infrastructure;

public static class DataGridAutoColumnWidthService
{
    private static bool _registered;

    private static readonly DependencyProperty IsRegisteredProperty =
        DependencyProperty.RegisterAttached(
            "IsRegistered",
            typeof(bool),
            typeof(DataGridAutoColumnWidthService),
            new PropertyMetadata(false));

    private static readonly DependencyProperty TrackedCollectionProperty =
        DependencyProperty.RegisterAttached(
            "TrackedCollection",
            typeof(INotifyCollectionChanged),
            typeof(DataGridAutoColumnWidthService),
            new PropertyMetadata(null));

    private static readonly DependencyProperty TrackedCollectionHandlerProperty =
        DependencyProperty.RegisterAttached(
            "TrackedCollectionHandler",
            typeof(NotifyCollectionChangedEventHandler),
            typeof(DataGridAutoColumnWidthService),
            new PropertyMetadata(null));

    public static void RegisterGlobal()
    {
        if (_registered)
            return;

        EventManager.RegisterClassHandler(
            typeof(DataGrid),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnDataGridLoaded));

        EventManager.RegisterClassHandler(
            typeof(DataGrid),
            FrameworkElement.UnloadedEvent,
            new RoutedEventHandler(OnDataGridUnloaded));

        _registered = true;
    }

    private static void OnDataGridLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        if (!(bool)grid.GetValue(IsRegisteredProperty))
        {
            grid.SetValue(IsRegisteredProperty, true);
            grid.DataContextChanged += OnDataGridDataContextChanged;
        }

        TrackItemsSource(grid);
        ScheduleAutoFit(grid);
    }

    private static void OnDataGridUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        UntrackItemsSource(grid);
        grid.DataContextChanged -= OnDataGridDataContextChanged;
        grid.SetValue(IsRegisteredProperty, false);
    }

    private static void OnDataGridDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        TrackItemsSource(grid);
        ScheduleAutoFit(grid);
    }

    private static void TrackItemsSource(DataGrid grid)
    {
        UntrackItemsSource(grid);

        if (grid.ItemsSource is not INotifyCollectionChanged collection)
            return;

        NotifyCollectionChangedEventHandler handler = (_, _) => ScheduleAutoFit(grid);
        collection.CollectionChanged += handler;
        grid.SetValue(TrackedCollectionProperty, collection);
        grid.SetValue(TrackedCollectionHandlerProperty, handler);
    }

    private static void UntrackItemsSource(DataGrid grid)
    {
        if (grid.GetValue(TrackedCollectionProperty) is INotifyCollectionChanged collection &&
            grid.GetValue(TrackedCollectionHandlerProperty) is NotifyCollectionChangedEventHandler handler)
        {
            collection.CollectionChanged -= handler;
        }

        grid.ClearValue(TrackedCollectionProperty);
        grid.ClearValue(TrackedCollectionHandlerProperty);
    }

    private static void ScheduleAutoFit(DataGrid grid)
    {
        if (!grid.IsLoaded)
            return;

        _ = grid.Dispatcher.BeginInvoke(
            new Action(() => ApplyAutoFit(grid)),
            DispatcherPriority.ContextIdle);
    }

    private static void ApplyAutoFit(DataGrid grid)
    {
        if (!grid.IsLoaded || grid.Columns.Count == 0)
            return;

        grid.MinColumnWidth = Math.Max(grid.MinColumnWidth, 48);
        grid.EnableColumnVirtualization = false;
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);

        foreach (var column in grid.Columns)
        {
            if (column.Visibility != Visibility.Visible)
                continue;

            var header = column.Header?.ToString() ?? string.Empty;
            column.MinWidth = Math.Max(column.MinWidth, ResolveMinimumWidth(header, column));
            column.Width = DataGridLength.Auto;
        }
    }

    private static double ResolveMinimumWidth(string header, DataGridColumn column)
    {
        if (column is DataGridCheckBoxColumn || ContainsAny(header, "선택", "체크"))
            return 46;

        if (string.Equals(header, "No", StringComparison.OrdinalIgnoreCase) ||
            ContainsAny(header, "번호", "순번"))
            return 58;

        if (ContainsAny(header, "일자", "날짜", "시각", "시간", "Date"))
            return 112;

        if (ContainsAny(header, "금액", "단가", "공급", "부가세", "합계", "잔액", "수금", "지불", "미수", "미지급", "입금", "출금"))
            return 98;

        if (ContainsAny(header, "거래처", "고객", "업체", "품목", "품명", "규격", "모델", "시리얼", "관리번호", "주소", "설치", "위치"))
            return 130;

        if (ContainsAny(header, "비고", "메모", "내용", "사유", "오류", "상세"))
            return 140;

        return 78;
    }

    private static bool ContainsAny(string value, params string[] keywords)
        => keywords.Any(keyword => value.Contains(keyword, StringComparison.CurrentCultureIgnoreCase));
}
