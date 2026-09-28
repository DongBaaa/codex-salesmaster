using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Infrastructure;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DeferredWindowKeyboardFocusTests
{
    [Theory]
    [InlineData("remembered")]
    [InlineData("first-control")]
    [InlineData("nonblocking")]
    [InlineData("other-window")]
    [InlineData("closed")]
    [InlineData("hidden")]
    [InlineData("initially-disabled")]
    public void DeferredLoadRestoresOnlyMissingFocusInItsActiveWindow(string scenario)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            Window? other = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var first = new TextBox { Name = "First", Height = 30 };
                var second = new TextBox { Name = "Second", Height = 30 };
                var panel = new StackPanel(); panel.Children.Add(first); panel.Children.Add(second);
                window = NewWindow(panel);
                if (scenario == "initially-disabled") window.IsEnabled = false;
                window.Loaded += (_, _) => second.Focus();
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var started = false;
                var blocking = scenario != "nonblocking";
                WindowShowHelper.ShowModelessWithDeferredLoad(window, async () =>
                {
                    started = true;
                    await completion.Task;
                }, "focus test", "focus test failure", blockWindowDuringLoad: blocking);
                PumpUntil(() => started);
                if (blocking) Assert.False(window.IsEnabled);
                if (scenario == "first-control") FocusManager.SetFocusedElement(window, null);
                if (scenario == "nonblocking") Assert.True(second.Focus());
                TextBox? otherInput = null;
                if (scenario == "other-window")
                {
                    otherInput = new TextBox { Height = 30 };
                    other = NewWindow(otherInput); other.Show(); other.Activate(); otherInput.Focus();
                    PumpUntil(() => other.IsActive && otherInput.IsKeyboardFocused);
                    Assert.False(window.IsActive);
                }
                if (scenario == "closed") window.Close();
                if (scenario == "hidden") window.Hide();
                completion.SetResult();
                PumpUntil(() => window.Cursor != Cursors.Wait);

                if (scenario == "remembered") Assert.Same(second, Keyboard.FocusedElement);
                if (scenario == "first-control") Assert.Same(first, Keyboard.FocusedElement);
                if (scenario == "nonblocking") Assert.Same(second, Keyboard.FocusedElement);
                if (scenario == "other-window")
                {
                    Assert.Same(otherInput, Keyboard.FocusedElement);
                    Assert.True(other!.IsActive);
                    Assert.False(window.IsActive);
                }
                if (scenario is "closed" or "hidden")
                {
                    Assert.False(window.IsVisible);
                    Assert.False(window.IsKeyboardFocusWithin);
                }
                if (scenario == "initially-disabled")
                {
                    Assert.False(window.IsEnabled);
                    Assert.False(window.IsKeyboardFocusWithin);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (other?.IsLoaded == true) other.Close();
                if (window?.IsLoaded == true) window.Close();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Focus regression timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Window NewWindow(object content) => new()
    {
        Title = "거래플랜 포커스 회귀 검사", Content = content,
        Width = 500, Height = 250, ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 550,
        Top = SystemParameters.VirtualScreenTop + 100
    };

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.True(condition(), "WPF focus/load state did not settle.");
    }
}
