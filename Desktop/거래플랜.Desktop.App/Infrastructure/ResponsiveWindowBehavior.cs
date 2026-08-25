using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace 거래플랜.Desktop.App.Infrastructure;

public static class ResponsiveWindowBehavior
{
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled",
            typeof(bool),
            typeof(ResponsiveWindowBehavior),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs eventArgs)
    {
        if (dependencyObject is not Window window)
            return;

        window.Initialized -= OnWindowInitialized;
        if (eventArgs.NewValue is not true)
            return;

        if (window.IsInitialized)
        {
            PrepareWindow(window);
            return;
        }

        window.Initialized += OnWindowInitialized;
    }

    private static void OnWindowInitialized(object? sender, EventArgs eventArgs)
    {
        if (sender is not Window window)
            return;

        window.Initialized -= OnWindowInitialized;
        PrepareWindow(window);
    }

    private static void PrepareWindow(Window window)
    {
        EnsureOverflowNavigation(window);
        ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(window);
    }

    private static void EnsureOverflowNavigation(Window window)
    {
        if (window.Content is not FrameworkElement content || content is ScrollViewer)
            return;

        var chromeWidth = Math.Max(
            0d,
            SystemParameters.ResizeFrameVerticalBorderWidth * 2d);
        var chromeHeight = Math.Max(
            0d,
            SystemParameters.CaptionHeight +
            (SystemParameters.ResizeFrameHorizontalBorderHeight * 2d));
        var minimumContentWidth = Math.Max(
            1d,
            ChildWindowResponsiveLayoutPolicy.MinimumContentWidthDip - chromeWidth);
        var minimumContentHeight = Math.Max(
            1d,
            ChildWindowResponsiveLayoutPolicy.MinimumContentHeightDip - chromeHeight);

        window.Content = null;
        var contentHost = new Grid
        {
            Width = minimumContentWidth,
            Height = minimumContentHeight
        };
        contentHost.Children.Add(content);

        var scrollViewer = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false,
            Focusable = false,
            Content = contentHost
        };

        void RefreshContentHostSize()
        {
            var requiredWidth = Math.Max(
                minimumContentWidth,
                scrollViewer.ViewportWidth);
            var requiredHeight = Math.Max(
                minimumContentHeight,
                scrollViewer.ViewportHeight);
            var descendantExtent = MeasureDescendantExtent(content);
            requiredWidth = Math.Max(requiredWidth, descendantExtent.Width);
            requiredHeight = Math.Max(requiredHeight, descendantExtent.Height);

            if (double.IsFinite(requiredWidth) &&
                Math.Abs(contentHost.Width - requiredWidth) > 0.5d)
                contentHost.Width = requiredWidth;
            if (double.IsFinite(requiredHeight) &&
                Math.Abs(contentHost.Height - requiredHeight) > 0.5d)
                contentHost.Height = requiredHeight;
        }

        scrollViewer.SizeChanged += (_, _) => RefreshContentHostSize();
        content.LayoutUpdated += (_, _) => RefreshContentHostSize();
        window.Content = scrollViewer;
        RefreshContentHostSize();
    }

    private static Size MeasureDescendantExtent(FrameworkElement root)
    {
        var maximumRight = root.ActualWidth;
        var maximumBottom = root.ActualHeight;
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            var childCount = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < childCount; index++)
            {
                var child = VisualTreeHelper.GetChild(current, index);
                if (child is not FrameworkElement descendant ||
                    !descendant.IsVisible ||
                    descendant.ActualWidth <= 0d ||
                    descendant.ActualHeight <= 0d)
                    continue;
                try
                {
                    var origin = descendant.TransformToAncestor(root).Transform(new Point(0d, 0d));
                    maximumRight = Math.Max(maximumRight, origin.X + descendant.ActualWidth);
                    maximumBottom = Math.Max(maximumBottom, origin.Y + descendant.ActualHeight);
                }
                catch (InvalidOperationException)
                {
                    // Popup visuals belong to another presentation source.
                }

                if (!IsNestedOverflowNavigationBoundary(descendant))
                    pending.Push(child);
            }
        }
        return new Size(maximumRight, maximumBottom);
    }

    private static bool IsNestedOverflowNavigationBoundary(FrameworkElement element) =>
        element is ScrollViewer or DataGrid or ListBox or ListView or TreeView;
}
