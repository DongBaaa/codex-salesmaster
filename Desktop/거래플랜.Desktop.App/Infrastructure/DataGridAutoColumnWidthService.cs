using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace 거래플랜.Desktop.App.Infrastructure;

public static class DataGridAutoColumnWidthService
{
    private const int AutoFitDebounceMilliseconds = 180;
    private const int MaxMeasuredRowsPerAutoFit = 120;
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

    private static readonly DependencyProperty PendingAutoFitProperty =
        DependencyProperty.RegisterAttached(
            "PendingAutoFit",
            typeof(bool),
            typeof(DataGridAutoColumnWidthService),
            new PropertyMetadata(false));

    private static readonly DependencyProperty AutoFitTimerProperty =
        DependencyProperty.RegisterAttached(
            "AutoFitTimer",
            typeof(DispatcherTimer),
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
            DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid))
                ?.AddValueChanged(grid, OnDataGridItemsSourceChanged);
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
        DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(DataGrid))
            ?.RemoveValueChanged(grid, OnDataGridItemsSourceChanged);
        if (grid.GetValue(AutoFitTimerProperty) is DispatcherTimer timer)
        {
            timer.Stop();
            grid.ClearValue(AutoFitTimerProperty);
        }

        grid.SetValue(IsRegisteredProperty, false);
        grid.SetValue(PendingAutoFitProperty, false);
    }

    private static void OnDataGridDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;

        TrackItemsSource(grid);
        ScheduleAutoFit(grid);
    }

    private static void OnDataGridItemsSourceChanged(object? sender, EventArgs e)
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

        grid.SetValue(PendingAutoFitProperty, true);

        var timer = grid.GetValue(AutoFitTimerProperty) as DispatcherTimer;
        if (timer is null)
        {
            timer = new DispatcherTimer(DispatcherPriority.Background, grid.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(AutoFitDebounceMilliseconds)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (!grid.IsLoaded)
                {
                    grid.SetValue(PendingAutoFitProperty, false);
                    return;
                }

                grid.SetValue(PendingAutoFitProperty, false);
                ApplyAutoFit(grid);
            };
            grid.SetValue(AutoFitTimerProperty, timer);
        }

        timer.Stop();
        timer.Start();
    }

    private static void ApplyAutoFit(DataGrid grid)
    {
        if (!grid.IsLoaded || grid.Columns.Count == 0)
            return;

        grid.MinColumnWidth = Math.Max(grid.MinColumnWidth, 48);
        grid.EnableColumnVirtualization = true;
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);

        foreach (var column in grid.Columns)
        {
            if (column.Visibility != Visibility.Visible)
                continue;

            var header = column.Header?.ToString() ?? string.Empty;
            var minimumWidth = ResolveMinimumWidth(header, column);
            var desiredWidth = ResolveDesiredColumnWidth(grid, column, header, minimumWidth);
            column.MinWidth = minimumWidth;
            column.Width = new DataGridLength(Math.Ceiling(desiredWidth));
        }
    }

    private static double ResolveDesiredColumnWidth(
        DataGrid grid,
        DataGridColumn column,
        string header,
        double minimumWidth)
    {
        var headerWidth = MeasureText(grid, header, grid.FontWeight) + 30;
        var desiredWidth = Math.Max(minimumWidth, headerWidth);

        if (column is DataGridCheckBoxColumn)
            return Math.Max(minimumWidth, headerWidth);

        if (column is not DataGridBoundColumn boundColumn ||
            boundColumn.Binding is not Binding binding)
        {
            return Math.Max(desiredWidth, ResolveFiniteColumnWidth(column));
        }

        foreach (var item in EnumerateItems(grid).Take(MaxMeasuredRowsPerAutoFit))
        {
            var text = ResolveBindingText(item, binding);
            if (string.IsNullOrEmpty(text))
                continue;

            desiredWidth = Math.Max(desiredWidth, MeasureText(grid, text, grid.FontWeight) + 28);
        }

        return Math.Max(desiredWidth, minimumWidth);
    }

    private static IEnumerable<object?> EnumerateItems(DataGrid grid)
    {
        var source = grid.ItemsSource ?? grid.Items;
        foreach (var item in source)
        {
            if (item == CollectionView.NewItemPlaceholder)
                continue;

            yield return item;
        }
    }

    private static string ResolveBindingText(object? item, Binding binding)
    {
        if (item is null)
            return string.Empty;

        var path = binding.Path?.Path;
        var value = string.IsNullOrWhiteSpace(path) || string.Equals(path, ".", StringComparison.Ordinal)
            ? item
            : ResolvePropertyPathValue(item, path);

        return FormatBindingValue(value, binding.StringFormat);
    }

    private static object? ResolvePropertyPathValue(object source, string path)
    {
        object? current = source;
        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current is null)
                return null;

            var segment = rawSegment;
            var indexStart = segment.IndexOf('[', StringComparison.Ordinal);
            if (indexStart >= 0)
                segment = segment[..indexStart];

            if (string.IsNullOrWhiteSpace(segment))
                continue;

            var descriptor = TypeDescriptor.GetProperties(current)[segment];
            if (descriptor is not null)
            {
                current = descriptor.GetValue(current);
                continue;
            }

            var property = current.GetType().GetProperty(segment);
            current = property?.GetValue(current);
        }

        return current;
    }

    private static string FormatBindingValue(object? value, string? stringFormat)
    {
        if (value is null)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(stringFormat))
        {
            var format = stringFormat.Trim();
            if (format.StartsWith("{}", StringComparison.Ordinal))
                format = format[2..];

            try
            {
                if (format.Contains("{0", StringComparison.Ordinal))
                    return string.Format(CultureInfo.CurrentCulture, format, value);

                if (value is IFormattable formattable)
                    return formattable.ToString(format, CultureInfo.CurrentCulture) ?? string.Empty;
            }
            catch (FormatException)
            {
                // 형식 문자열이 WPF 전용 표현이면 기본 문자열로 안전하게 되돌린다.
            }
        }

        return Convert.ToString(value, CultureInfo.CurrentCulture) ?? string.Empty;
    }

    private static double ResolveFiniteColumnWidth(DataGridColumn column)
    {
        var displayValue = column.ActualWidth > 0 ? column.ActualWidth : column.Width.DisplayValue;
        return double.IsNaN(displayValue) || double.IsInfinity(displayValue) ? 0 : displayValue;
    }

    private static double MeasureText(DataGrid grid, string text, FontWeight fontWeight)
    {
        if (string.IsNullOrEmpty(text))
            return 0;

        var dpi = VisualTreeHelper.GetDpi(grid);
        var formattedText = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(grid.FontFamily, grid.FontStyle, fontWeight, grid.FontStretch),
            grid.FontSize,
            Brushes.Black,
            dpi.PixelsPerDip);
        return formattedText.WidthIncludingTrailingWhitespace;
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
