using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace 거래플랜.Desktop.App.Infrastructure;

internal static class MainWindowResponsiveLayoutPolicy
{
    internal const double PreferredWidthDip = 1800d;
    internal const double PreferredHeightDip = 860d;
    internal const double MinimumWidthDip = 640d;
    internal const double MinimumHeightDip = 320d;
    internal const double MinimumContentWidthDip = 760d;
    internal const double MinimumContentHeightDip = 560d;
    internal const double WorkAreaInsetDip = 16d;
    internal const double CompactLayoutWidthThresholdDip = 1200d;
    internal const double CompactLayoutHeightThresholdDip = 600d;

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint SetWindowPosNoZOrder = 0x0004;
    private const uint SetWindowPosNoActivate = 0x0010;

    internal static Size ResolveInitialWindowSize(Rect workArea)
    {
        if (!IsFinitePositive(workArea.Width) || !IsFinitePositive(workArea.Height))
            return new Size(PreferredWidthDip, PreferredHeightDip);

        var availableWidth = Math.Max(MinimumWidthDip, workArea.Width - WorkAreaInsetDip);
        var availableHeight = Math.Max(MinimumHeightDip, workArea.Height - WorkAreaInsetDip);

        return new Size(
            Math.Min(PreferredWidthDip, availableWidth),
            Math.Min(PreferredHeightDip, availableHeight));
    }

    internal static Rect ResolveInitialWindowBounds(Rect workArea)
    {
        var size = ResolveInitialWindowSize(workArea);
        if (!IsFinitePositive(workArea.Width) ||
            !IsFinitePositive(workArea.Height))
        {
            return new Rect(new Point(0, 0), size);
        }

        return new Rect(
            workArea.Left + ((workArea.Width - size.Width) / 2d),
            workArea.Top + ((workArea.Height - size.Height) / 2d),
            size.Width,
            size.Height);
    }

    internal static Rect ResolvePhysicalWindowBounds(
        Rect physicalWorkArea,
        double scale)
    {
        if (!IsFinitePositive(physicalWorkArea.Width) ||
            !IsFinitePositive(physicalWorkArea.Height) ||
            !IsFinitePositive(scale))
        {
            return Rect.Empty;
        }

        var logicalBounds = ResolveInitialWindowBounds(
            new Rect(
                0,
                0,
                physicalWorkArea.Width / scale,
                physicalWorkArea.Height / scale));
        var targetWidth = Math.Min(
            physicalWorkArea.Width,
            Math.Max(1d, Math.Round(logicalBounds.Width * scale)));
        var targetHeight = Math.Min(
            physicalWorkArea.Height,
            Math.Max(1d, Math.Round(logicalBounds.Height * scale)));

        return new Rect(
            physicalWorkArea.Left +
            ((physicalWorkArea.Width - targetWidth) / 2d),
            physicalWorkArea.Top +
            ((physicalWorkArea.Height - targetHeight) / 2d),
            targetWidth,
            targetHeight);
    }

    internal static bool ShouldUseCompactLayout(Size clientSize) =>
        IsFinitePositive(clientSize.Width) &&
        IsFinitePositive(clientSize.Height) &&
        (clientSize.Width < CompactLayoutWidthThresholdDip ||
         clientSize.Height < CompactLayoutHeightThresholdDip);

    internal static bool ShouldUseContentScrollFallback(Size clientSize) =>
        IsFinitePositive(clientSize.Width) &&
        IsFinitePositive(clientSize.Height) &&
        (clientSize.Width < MinimumContentWidthDip ||
         clientSize.Height < MinimumContentHeightDip);

    internal static void ApplyInitialWindowSize(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        ApplyWindowSize(window, SystemParameters.WorkArea);

        EventHandler? sourceInitializedHandler = null;
        sourceInitializedHandler = (_, _) =>
        {
            window.SourceInitialized -= sourceInitializedHandler;
            ApplyActualMonitorPlacement(window);
        };
        window.SourceInitialized += sourceInitializedHandler;
    }

    private static void ApplyWindowSize(Window window, Rect workArea)
    {
        var size = ResolveInitialWindowSize(workArea);
        window.Width = size.Width;
        window.Height = size.Height;
    }

    private static void ApplyActualMonitorPlacement(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;

        var monitor =
            GetCursorPos(out var cursorPosition)
                ? MonitorFromPoint(
                    cursorPosition,
                    MonitorDefaultToNearest)
                : MonitorFromWindow(
                    handle,
                    MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
            return;

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return;

        var dpi = TryGetMonitorDpi(monitor, out var monitorDpi)
            ? monitorDpi
            : GetDpiForWindow(handle);
        if (dpi == 0)
            return;

        var scale = dpi / 96d;
        var physicalWidth =
            monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        var physicalHeight =
            monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        if (physicalWidth <= 0 || physicalHeight <= 0)
            return;

        var physicalBounds = ResolvePhysicalWindowBounds(
            new Rect(
                monitorInfo.WorkArea.Left,
                monitorInfo.WorkArea.Top,
                physicalWidth,
                physicalHeight),
            scale);
        if (physicalBounds.IsEmpty)
            return;

        window.Width = physicalBounds.Width / scale;
        window.Height = physicalBounds.Height / scale;
        window.WindowStartupLocation = WindowStartupLocation.Manual;

        _ = SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(physicalBounds.Left),
            (int)Math.Round(physicalBounds.Top),
            (int)Math.Round(physicalBounds.Width),
            (int)Math.Round(physicalBounds.Height),
            SetWindowPosNoZOrder | SetWindowPosNoActivate);
    }

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
