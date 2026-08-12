using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace 거래플랜.Desktop.App.Infrastructure;

internal static class DialogWindowCloseHelper
{
    private static readonly object DialogGate = new();
    private static readonly HashSet<Window> ActiveDialogs = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<CommonDialog> ActiveNativeDialogs = new(ReferenceEqualityComparer.Instance);
    private static TaskCompletionSource NativeDialogsDrained = CreateCompletedNativeDialogDrainSource();

    public static bool? ShowDialog(Window window, bool allowDuringShutdown = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!allowDuringShutdown &&
            Application.Current?.MainWindow is global::거래플랜.Desktop.App.MainWindow mainWindow &&
            mainWindow.IsShutdownProtectionActive)
        {
            return false;
        }

        lock (DialogGate)
            ActiveDialogs.Add(window);

        try
        {
            return window.ShowDialog();
        }
        finally
        {
            lock (DialogGate)
                ActiveDialogs.Remove(window);
        }
    }

    public static Window[] SnapshotActiveDialogs()
    {
        lock (DialogGate)
            return ActiveDialogs.Where(window => window.IsLoaded).ToArray();
    }

    public static int ActiveNativeDialogCount
    {
        get
        {
            lock (DialogGate)
                return ActiveNativeDialogs.Count;
        }
    }

    public static bool? ShowDialog(
        CommonDialog dialog,
        Window? owner = null,
        bool allowDuringShutdown = false)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (!allowDuringShutdown &&
            Application.Current?.MainWindow is global::거래플랜.Desktop.App.MainWindow mainWindow &&
            mainWindow.IsShutdownProtectionActive)
        {
            return false;
        }

        var registered = false;
        lock (DialogGate)
        {
            if (ActiveNativeDialogs.Count == 0)
            {
                NativeDialogsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            registered = ActiveNativeDialogs.Add(dialog);
        }

        try
        {
            return owner is null
                ? dialog.ShowDialog()
                : dialog.ShowDialog(owner);
        }
        finally
        {
            TaskCompletionSource? drained = null;
            lock (DialogGate)
            {
                if (registered &&
                    ActiveNativeDialogs.Remove(dialog) &&
                    ActiveNativeDialogs.Count == 0)
                {
                    drained = NativeDialogsDrained;
                }
            }

            drained?.TrySetResult();
        }
    }

    public static Task WaitForNoActiveNativeDialogsAsync()
    {
        lock (DialogGate)
        {
            return ActiveNativeDialogs.Count == 0
                ? Task.CompletedTask
                : NativeDialogsDrained.Task;
        }
    }

    public static void Close(Window window, bool? dialogResult = null)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (dialogResult.HasValue && TrySetDialogResult(window, dialogResult.Value))
            return;

        window.Close();
    }

    public static bool TrySetDialogResult(Window window, bool dialogResult)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!window.IsLoaded || !window.IsVisible || PresentationSource.FromVisual(window) is null)
            return false;

        if (!ComponentDispatcher.IsThreadModal)
            return false;

        try
        {
            window.DialogResult = dialogResult;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static TaskCompletionSource CreateCompletedNativeDialogDrainSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
