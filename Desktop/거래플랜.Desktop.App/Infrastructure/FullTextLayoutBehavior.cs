using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace 거래플랜.Desktop.App.Infrastructure;

public static class FullTextLayoutBehavior
{
    static FullTextLayoutBehavior()
    {
        foreach (var elementType in new[]
                 {
                     typeof(TextBlock),
                     typeof(AccessText),
                     typeof(Button),
                     typeof(DataGrid),
                     typeof(DataGridColumnHeader)
                 })
        {
            EventManager.RegisterClassHandler(
                elementType,
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnAnyFrameworkElementLoaded),
                handledEventsToo: true);
        }
    }

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(FullTextLayoutBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static readonly DependencyProperty PreserveSingleLineProperty =
        DependencyProperty.RegisterAttached(
            "PreserveSingleLine",
            typeof(bool),
            typeof(FullTextLayoutBehavior),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetPreserveSingleLine(DependencyObject element) =>
        (bool)element.GetValue(PreserveSingleLineProperty);

    public static void SetPreserveSingleLine(DependencyObject element, bool value) =>
        element.SetValue(PreserveSingleLineProperty, value);

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not Window window)
            return;

        window.RemoveHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler(OnDescendantLoaded));
        window.Loaded -= OnWindowLoaded;
        if (eventArgs.NewValue is not true)
            return;

        window.AddHandler(
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnDescendantLoaded),
            handledEventsToo: true);
        window.Loaded += OnWindowLoaded;
        if (window.IsLoaded)
            ApplyToSubtree(window);
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is Window window)
            ApplyToSubtree(window);
    }

    private static void OnAnyFrameworkElementLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not DependencyObject loaded)
            return;

        ApplyToSubtree(loaded);
    }

    private static void OnDescendantLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (eventArgs.OriginalSource is DependencyObject loaded)
            ApplyToSubtree(loaded);
    }

    private static void ApplyToSubtree(DependencyObject root)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            ApplyFullTextContract(current);

            if (current is not Visual && current is not Visual3D)
                continue;

            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < childCount; index++)
                pending.Push(VisualTreeHelper.GetChild(current, index));
        }
    }

    private static void ApplyFullTextContract(DependencyObject element)
    {
        var preserveSingleLine = GetPreserveSingleLine(element);
        if (!preserveSingleLine && element is TextBlock textBlock)
        {
            textBlock.SetCurrentValue(TextBlock.TextTrimmingProperty, TextTrimming.None);
            textBlock.SetCurrentValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        }
        else if (!preserveSingleLine && element is AccessText accessText)
        {
            accessText.SetCurrentValue(AccessText.TextWrappingProperty, TextWrapping.Wrap);
        }

        if (!preserveSingleLine && element is DataGrid dataGrid)
        {
            dataGrid.SetCurrentValue(DataGrid.RowHeightProperty, double.NaN);
            dataGrid.SetCurrentValue(DataGrid.ColumnHeaderHeightProperty, double.NaN);
            dataGrid.SetCurrentValue(DataGrid.MinRowHeightProperty, Math.Max(32d, dataGrid.MinRowHeight));
        }

        if (element is DataGridColumnHeader header)
            header.SetCurrentValue(FrameworkElement.MinHeightProperty, Math.Max(36d, header.MinHeight));

        if (element is Button button && button.Content is string)
        {
            var isCompactSquare = double.IsFinite(button.Width) &&
                                  double.IsFinite(button.Height) &&
                                  button.Width <= 40d &&
                                  button.Height <= 40d;
            if (!isCompactSquare && double.IsFinite(button.Height))
                button.SetCurrentValue(FrameworkElement.HeightProperty, double.NaN);
            if (!isCompactSquare)
                button.SetCurrentValue(FrameworkElement.MinHeightProperty, Math.Max(38d, button.MinHeight));
        }
    }
}
