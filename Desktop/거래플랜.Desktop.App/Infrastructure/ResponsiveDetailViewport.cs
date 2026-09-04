using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace 거래플랜.Desktop.App.Infrastructure;

/// <summary>
/// Keeps opt-in form content inside the visible width of its ScrollViewer.
/// The calculation uses ViewportWidth, which already excludes a visible
/// vertical scrollbar, and deliberately leaves table/list scroll owners alone.
/// </summary>
public static class ResponsiveDetailViewport
{
    public static readonly DependencyProperty ConstrainContentWidthProperty =
        DependencyProperty.RegisterAttached(
            "ConstrainContentWidth",
            typeof(bool),
            typeof(ResponsiveDetailViewport),
            new PropertyMetadata(false, OnConstrainContentWidthChanged));

    public static bool GetConstrainContentWidth(DependencyObject element)
        => (bool)element.GetValue(ConstrainContentWidthProperty);

    public static void SetConstrainContentWidth(DependencyObject element, bool value)
        => element.SetValue(ConstrainContentWidthProperty, value);

    private static void OnConstrainContentWidthChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not ScrollViewer scrollViewer)
            return;

        if (args.OldValue is true)
        {
            scrollViewer.Loaded -= HandleLayoutChanged;
            scrollViewer.SizeChanged -= HandleSizeChanged;
            scrollViewer.ScrollChanged -= HandleScrollChanged;
        }

        if (args.NewValue is not true)
            return;

        scrollViewer.Loaded += HandleLayoutChanged;
        scrollViewer.SizeChanged += HandleSizeChanged;
        scrollViewer.ScrollChanged += HandleScrollChanged;
        Apply(scrollViewer);
    }

    private static void HandleLayoutChanged(object sender, RoutedEventArgs args)
        => Apply((ScrollViewer)sender);

    private static void HandleSizeChanged(object sender, SizeChangedEventArgs args)
        => Apply((ScrollViewer)sender);

    private static void HandleScrollChanged(object sender, ScrollChangedEventArgs args)
    {
        if (Math.Abs(args.ViewportWidthChange) > double.Epsilon)
            Apply((ScrollViewer)sender);
    }

    private static void Apply(ScrollViewer scrollViewer)
    {
        if (!scrollViewer.IsLoaded ||
            scrollViewer.Content is not FrameworkElement content ||
            !double.IsFinite(scrollViewer.ViewportWidth) ||
            scrollViewer.ViewportWidth <= 0d)
        {
            return;
        }

        var horizontalMargin = content.Margin.Left + content.Margin.Right;
        var availableWidth = Math.Max(0d, scrollViewer.ViewportWidth - horizontalMargin);

        content.SetCurrentValue(FrameworkElement.MinWidthProperty, 0d);
        content.SetCurrentValue(FrameworkElement.WidthProperty, availableWidth);
        content.SetCurrentValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);

        NormalizeResponsiveDescendants(content, availableWidth);
    }

    private static void NormalizeResponsiveDescendants(DependencyObject root, double availableWidth)
    {
        if (root is Grid grid)
        {
            foreach (var column in grid.ColumnDefinitions.Where(column => column.Width.IsStar))
                column.MinWidth = 0d;
        }

        if (root is TextBox or ComboBox or DatePicker or PasswordBox)
        {
            var control = (FrameworkElement)root;
            control.SetCurrentValue(FrameworkElement.MinWidthProperty, 0d);
            control.SetCurrentValue(FrameworkElement.MaxWidthProperty, availableWidth);

            if (double.IsNaN(control.Width))
            {
                control.SetCurrentValue(
                    FrameworkElement.HorizontalAlignmentProperty,
                    HorizontalAlignment.Stretch);
            }
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
            NormalizeResponsiveDescendants(VisualTreeHelper.GetChild(root, index), availableWidth);
    }
}
