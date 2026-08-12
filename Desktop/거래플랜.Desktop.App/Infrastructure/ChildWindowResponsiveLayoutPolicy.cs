using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace 거래플랜.Desktop.App.Infrastructure;

internal static class ChildWindowResponsiveLayoutPolicy
{
    internal const double MinimumWidthDip = 640d;
    internal const double MinimumHeightDip = 400d;
    internal const double WorkAreaInsetDip = 16d;

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;
    private const int WindowDpiChangedMessage = 0x02E0;

    internal static Size ResolveInitialWindowSize(
        Size preferredSize,
        Size declaredMinimumSize,
        Rect logicalWorkArea)
    {
        var preferredWidth = NormalizeDimension(
            preferredSize.Width,
            MinimumWidthDip);
        var preferredHeight = NormalizeDimension(
            preferredSize.Height,
            MinimumHeightDip);
        var minimumWidth = NormalizeDimension(
            declaredMinimumSize.Width,
            MinimumWidthDip);
        var minimumHeight = NormalizeDimension(
            declaredMinimumSize.Height,
            MinimumHeightDip);

        if (!IsFinitePositive(logicalWorkArea.Width) ||
            !IsFinitePositive(logicalWorkArea.Height))
        {
            return new Size(
                Math.Max(preferredWidth, minimumWidth),
                Math.Max(preferredHeight, minimumHeight));
        }

        var availableWidth = Math.Max(
            1d,
            logicalWorkArea.Width - WorkAreaInsetDip);
        var availableHeight = Math.Max(
            1d,
            logicalWorkArea.Height - WorkAreaInsetDip);
        var effectiveMinimumWidth = Math.Min(
            minimumWidth,
            availableWidth);
        var effectiveMinimumHeight = Math.Min(
            minimumHeight,
            availableHeight);

        return new Size(
            Math.Clamp(
                preferredWidth,
                effectiveMinimumWidth,
                availableWidth),
            Math.Clamp(
                preferredHeight,
                effectiveMinimumHeight,
                availableHeight));
    }

    internal static Rect ResolvePhysicalWindowBounds(
        Rect physicalWorkArea,
        double scale,
        Size preferredSize,
        Size declaredMinimumSize)
    {
        if (!IsFinitePositive(physicalWorkArea.Width) ||
            !IsFinitePositive(physicalWorkArea.Height) ||
            !IsFinitePositive(scale))
        {
            return Rect.Empty;
        }

        var logicalSize = ResolveInitialWindowSize(
            preferredSize,
            declaredMinimumSize,
            new Rect(
                0d,
                0d,
                physicalWorkArea.Width / scale,
                physicalWorkArea.Height / scale));
        var physicalWidth = Math.Min(
            physicalWorkArea.Width,
            Math.Max(1d, Math.Round(logicalSize.Width * scale)));
        var physicalHeight = Math.Min(
            physicalWorkArea.Height,
            Math.Max(1d, Math.Round(logicalSize.Height * scale)));

        return new Rect(
            physicalWorkArea.Left +
            ((physicalWorkArea.Width - physicalWidth) / 2d),
            physicalWorkArea.Top +
            ((physicalWorkArea.Height - physicalHeight) / 2d),
            physicalWidth,
            physicalHeight);
    }

    internal static void ApplyInitialWindowSize(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var preferredSize = new Size(
            NormalizeDimension(window.Width, MinimumWidthDip),
            NormalizeDimension(window.Height, MinimumHeightDip));
        var declaredMinimumSize = new Size(
            NormalizeDimension(window.MinWidth, MinimumWidthDip),
            NormalizeDimension(window.MinHeight, MinimumHeightDip));

        ApplyLogicalWindowSize(
            window,
            preferredSize,
            declaredMinimumSize,
            SystemParameters.WorkArea);

        EventHandler? sourceInitializedHandler = null;
        sourceInitializedHandler = (_, _) =>
        {
            window.SourceInitialized -= sourceInitializedHandler;
            InitializeNativePlacement(
                window,
                preferredSize,
                declaredMinimumSize);
        };
        window.SourceInitialized += sourceInitializedHandler;
    }

    private static void ApplyLogicalWindowSize(
        Window window,
        Size preferredSize,
        Size declaredMinimumSize,
        Rect logicalWorkArea)
    {
        var size = ResolveInitialWindowSize(
            preferredSize,
            declaredMinimumSize,
            logicalWorkArea);
        window.MinWidth = Math.Min(declaredMinimumSize.Width, size.Width);
        window.MinHeight = Math.Min(declaredMinimumSize.Height, size.Height);
        window.Width = size.Width;
        window.Height = size.Height;
    }

    private static void InitializeNativePlacement(
        Window window,
        Size preferredSize,
        Size declaredMinimumSize)
    {
        var windowHandle = new WindowInteropHelper(window).Handle;
        if (windowHandle == IntPtr.Zero)
            return;

        var monitor = ResolveOwnerOrWindowMonitor(window, windowHandle);
        if (monitor == IntPtr.Zero)
            return;

        ApplyMonitorPlacement(
            window,
            windowHandle,
            monitor,
            preferredSize,
            declaredMinimumSize);
        AttachMonitorTransitionTracking(
            window,
            windowHandle,
            declaredMinimumSize);
    }

    private static void ApplyMonitorPlacement(
        Window window,
        IntPtr windowHandle,
        IntPtr monitor,
        Size preferredSize,
        Size declaredMinimumSize)
    {

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return;

        var dpi = TryGetMonitorDpi(monitor, out var monitorDpi)
            ? monitorDpi
            : GetDpiForWindow(windowHandle);
        if (dpi == 0)
            return;

        var physicalWidth =
            monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        var physicalHeight =
            monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        if (physicalWidth <= 0 || physicalHeight <= 0)
            return;

        var scale = dpi / 96d;
        var physicalBounds = ResolvePhysicalWindowBounds(
            new Rect(
                monitorInfo.WorkArea.Left,
                monitorInfo.WorkArea.Top,
                physicalWidth,
                physicalHeight),
            scale,
            preferredSize,
            declaredMinimumSize);
        if (physicalBounds.IsEmpty)
            return;

        var logicalWidth = physicalBounds.Width / scale;
        var logicalHeight = physicalBounds.Height / scale;
        window.MinWidth = Math.Min(
            declaredMinimumSize.Width,
            logicalWidth);
        window.MinHeight = Math.Min(
            declaredMinimumSize.Height,
            logicalHeight);
        window.Width = logicalWidth;
        window.Height = logicalHeight;
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        _ = SetWindowPos(
            windowHandle,
            IntPtr.Zero,
            (int)Math.Round(physicalBounds.Left),
            (int)Math.Round(physicalBounds.Top),
            (int)Math.Round(physicalBounds.Width),
            (int)Math.Round(physicalBounds.Height),
            SetWindowPosNoZOrder | SetWindowPosNoActivate);
    }

    private static void AttachMonitorTransitionTracking(
        Window window,
        IntPtr windowHandle,
        Size declaredMinimumSize)
    {
        var source = HwndSource.FromHwnd(windowHandle);
        if (source is null)
            return;

        var trackedMonitor = MonitorFromWindow(
            windowHandle,
            MonitorDefaultToNearest);
        var placementQueued = false;

        void QueuePlacementOnCurrentMonitor()
        {
            if (placementQueued || window.Dispatcher.HasShutdownStarted)
                return;

            placementQueued = true;
            _ = window.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() =>
                {
                    placementQueued = false;
                    if (window.Dispatcher.HasShutdownStarted)
                        return;

                    var currentMonitor = MonitorFromWindow(
                        windowHandle,
                        MonitorDefaultToNearest);
                    if (currentMonitor == IntPtr.Zero)
                        return;

                    trackedMonitor = currentMonitor;
                    var currentPreferredSize = new Size(
                        NormalizeDimension(
                            window.ActualWidth,
                            NormalizeDimension(
                                window.Width,
                                MinimumWidthDip)),
                        NormalizeDimension(
                            window.ActualHeight,
                            NormalizeDimension(
                                window.Height,
                                MinimumHeightDip)));
                    ApplyMonitorPlacement(
                        window,
                        windowHandle,
                        currentMonitor,
                        currentPreferredSize,
                        declaredMinimumSize);
                }));
        }

        EventHandler locationChangedHandler = (_, _) =>
        {
            var currentMonitor = MonitorFromWindow(
                windowHandle,
                MonitorDefaultToNearest);
            if (currentMonitor == IntPtr.Zero ||
                currentMonitor == trackedMonitor)
            {
                return;
            }

            trackedMonitor = currentMonitor;
            QueuePlacementOnCurrentMonitor();
        };
        HwndSourceHook dpiChangedHook = (
            IntPtr _,
            int message,
            IntPtr _,
            IntPtr _,
            ref bool _) =>
        {
            if (message == WindowDpiChangedMessage)
                QueuePlacementOnCurrentMonitor();

            return IntPtr.Zero;
        };
        EventHandler? closedHandler = null;
        closedHandler = (_, _) =>
        {
            window.LocationChanged -= locationChangedHandler;
            window.Closed -= closedHandler;
            if (!source.IsDisposed)
                source.RemoveHook(dpiChangedHook);
        };

        window.LocationChanged += locationChangedHandler;
        source.AddHook(dpiChangedHook);
        window.Closed += closedHandler;
    }

    private static IntPtr ResolveOwnerOrWindowMonitor(
        Window window,
        IntPtr windowHandle)
    {
        if (window.Owner is not null)
        {
            var ownerHandle = new WindowInteropHelper(window.Owner).Handle;
            if (ownerHandle != IntPtr.Zero)
            {
                var ownerMonitor = MonitorFromWindow(
                    ownerHandle,
                    MonitorDefaultToNearest);
                if (ownerMonitor != IntPtr.Zero)
                    return ownerMonitor;
            }
        }

        var windowMonitor = MonitorFromWindow(
            windowHandle,
            MonitorDefaultToNearest);
        if (windowMonitor != IntPtr.Zero)
            return windowMonitor;

        return GetCursorPos(out var cursorPosition)
            ? MonitorFromPoint(
                cursorPosition,
                MonitorDefaultToNearest)
            : IntPtr.Zero;
    }

    private static double NormalizeDimension(
        double value,
        double fallback) =>
        IsFinitePositive(value) ? value : fallback;

    private static bool IsFinitePositive(double value) =>
        double.IsFinite(value) && value > 0d;

    private static bool TryGetMonitorDpi(
        IntPtr monitor,
        out uint dpi)
    {
        dpi = 0;
        try
        {
            var result = GetDpiForMonitor(
                monitor,
                MonitorDpiType.Effective,
                out var dpiX,
                out var dpiY);
            if (result != 0 || dpiX == 0 || dpiY == 0)
                return false;

            dpi = dpiX;
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(
        IntPtr windowHandle,
        uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(
        NativePoint point,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(
        out NativePoint point);

    [DllImport(
        "user32.dll",
        CharSet = CharSet.Auto,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitorHandle,
        ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr windowHandle);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        IntPtr monitorHandle,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr windowHandle,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private enum MonitorDpiType
    {
        Effective = 0
    }
}
