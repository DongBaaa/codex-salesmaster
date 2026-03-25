using System.Windows;
using System.Windows.Interop;

namespace 거래플랜.Desktop.App.Infrastructure;

internal static class DialogWindowCloseHelper
{
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
}
