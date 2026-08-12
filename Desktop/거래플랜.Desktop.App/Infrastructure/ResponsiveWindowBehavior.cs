using System.Windows;

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
        if (dependencyObject is not Window window ||
            window is global::거래플랜.Desktop.App.MainWindow)
        {
            return;
        }

        window.Initialized -= OnWindowInitialized;
        if (eventArgs.NewValue is not true)
            return;

        if (window.IsInitialized)
        {
            ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(window);
            return;
        }

        window.Initialized += OnWindowInitialized;
    }

    private static void OnWindowInitialized(object? sender, EventArgs eventArgs)
    {
        if (sender is not Window window)
            return;

        window.Initialized -= OnWindowInitialized;
        ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(window);
    }
}
