using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace 거래플랜.Desktop.App.Services;

public static class PreviewPrintCommandRouting
{
    public static void Bind(Window window, DocumentViewer viewer, ICommand printCommand)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(printCommand);

        // Override DocumentViewer's native print route so its toolbar uses the same options.
        viewer.CommandBindings.Add(new CommandBinding(ApplicationCommands.Print,
            (_, args) =>
            {
                args.Handled = true;
                if (printCommand.CanExecute(null))
                    printCommand.Execute(null);
            },
            (_, args) =>
            {
                args.CanExecute = printCommand.CanExecute(null);
                args.Handled = true;
            }));
        window.InputBindings.Add(new KeyBinding(printCommand, Key.P, ModifierKeys.Control));
    }
}
