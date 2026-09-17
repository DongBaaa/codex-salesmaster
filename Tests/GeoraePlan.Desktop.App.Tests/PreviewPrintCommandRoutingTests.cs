using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class PreviewPrintCommandRoutingTests
{
    [Fact]
    public void ViewerPrint_UsesApplicationCommand_AndHonorsBusyGuard()
    {
        RunOnSta(() =>
        {
            var window = new Window();
            var viewer = new DocumentViewer();
            window.Content = viewer;
            var calls = 0;
            var busy = false;
            var command = new RelayCommand(() => calls++, () => !busy);
            PreviewPrintCommandRouting.Bind(window, viewer, command);

            Assert.True(ApplicationCommands.Print.CanExecute(null, viewer));
            ApplicationCommands.Print.Execute(null, viewer);
            Assert.Equal(1, calls);

            busy = true;
            command.NotifyCanExecuteChanged();
            Assert.False(ApplicationCommands.Print.CanExecute(null, viewer));
            ApplicationCommands.Print.Execute(null, viewer);
            Assert.Equal(1, calls);

            busy = false;
            command.NotifyCanExecuteChanged();
            ApplicationCommands.Print.Execute(null, viewer);
            Assert.Equal(2, calls);
            window.Close();
        });
    }

    [Fact]
    public void WindowShortcut_UsesSameCommand_OutsideDocumentFocus()
    {
        RunOnSta(() =>
        {
            var window = new Window();
            var panel = new StackPanel();
            var viewer = new DocumentViewer();
            var closeButton = new Button();
            panel.Children.Add(viewer);
            panel.Children.Add(closeButton);
            window.Content = panel;
            var calls = 0;
            var command = new RelayCommand(() => calls++);
            PreviewPrintCommandRouting.Bind(window, viewer, command);

            var shortcut = Assert.Single(window.InputBindings.OfType<KeyBinding>());
            Assert.Equal(Key.P, shortcut.Key);
            Assert.Equal(ModifierKeys.Control, shortcut.Modifiers);
            Assert.Same(command, shortcut.Command);
            shortcut.Command.Execute(null);
            ApplicationCommands.Print.Execute(null, viewer);
            Assert.Equal(2, calls);
            window.Close();
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Print command routing timed out.");
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }
}
