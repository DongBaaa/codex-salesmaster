using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class EnvironmentSettingsTabNavigationTests
{
    [Theory]
    [InlineData(false, false, true, true, false)]
    [InlineData(true, true, true, false, false)]
    [InlineData(false, true, false, false, true)]
    public void SettingsTabs_SeparateInitialNavigationFromProtectedEditing(
        bool initializing, bool busy, bool navigationEnabled, bool editorEnabled, bool closeBlocked)
    {
        OnSta(() =>
        {
            var vm = CreateStateOnlyViewModel();
            vm.IsInitialLoadInProgress = initializing;
            vm.IsBusy = busy;
            var tabs = LoadActualTabEnableBindings(vm);
            tabs.ApplyTemplate();
            tabs.Measure(new Size(900, 600));
            tabs.Arrange(new Rect(0, 0, 900, 600));
            tabs.UpdateLayout();
            DrainBindings();
            Assert.Equal(navigationEnabled, tabs.IsEnabled);
            Assert.Equal(closeBlocked, vm.IsCloseBlocked);
            foreach (TabItem tab in tabs.Items)
            {
                tabs.SelectedItem = tab;
                tabs.UpdateLayout();
                DrainBindings();
                Assert.Equal(navigationEnabled, tab.IsEnabled);
                Assert.Equal(editorEnabled, ((FrameworkElement)tab.Content).IsEnabled);
            }
        });
    }

    [Fact]
    public void FinishingInitialLoad_AndStartingAWrite_UpdatesAlreadyBoundControls()
    {
        OnSta(() =>
        {
            var vm = CreateStateOnlyViewModel();
            var tabs = LoadActualTabEnableBindings(vm);
            tabs.ApplyTemplate();
            tabs.Measure(new Size(900, 600));
            tabs.Arrange(new Rect(0, 0, 900, 600));
            vm.IsInitialLoadInProgress = true;
            vm.IsBusy = true;
            DrainBindings();
            Assert.True(tabs.IsEnabled);
            Assert.False(((FrameworkElement)((TabItem)tabs.Items[0]).Content).IsEnabled);
            vm.IsBusy = false;
            vm.IsInitialLoadInProgress = false;
            DrainBindings();
            Assert.True(tabs.IsEnabled);
            Assert.True(((FrameworkElement)((TabItem)tabs.Items[0]).Content).IsEnabled);
            vm.IsBusy = true;
            DrainBindings();
            Assert.False(tabs.IsEnabled);
            Assert.True(vm.IsCloseBlocked);
            vm.IsBusy = false;
            DrainBindings();
            Assert.True(tabs.IsEnabled);
        });
    }

    // No InitializeAsync, database, backup enumeration, network or operational App is started.
    private static EnvironmentSettingsViewModel CreateStateOnlyViewModel() =>
        new(null!, new SessionState(), null!, null!, null!, null!, null!, null!, null!, null!, null!);

    private static TabControl LoadActualTabEnableBindings(object viewModel, [CallerFilePath] string sourcePath = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
        var source = XDocument.Load(Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "Views", "EnvironmentSettingsWindow.xaml"));
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var original = source.Descendants(w + "TabControl").Single(e => (string?)e.Attribute(x + "Name") == "SettingsTabs");
        var fragment = new XElement(w + "TabControl", new XAttribute("IsEnabled", (string)original.Attribute("IsEnabled")!));
        var originals = original.Elements(w + "TabItem").ToArray();
        Assert.Equal(7, originals.Length);
        foreach (var tab in originals)
        {
            var content = tab.Elements().Single();
            var panel = new XElement(w + "Grid", new XElement(w + "Button", new XAttribute("Content", "protected action")));
            if (content.Attribute("IsEnabled") is { } enabled)
                panel.SetAttributeValue("IsEnabled", enabled.Value);
            fragment.Add(new XElement(w + "TabItem", new XAttribute("Header", (string)tab.Attribute("Header")!), panel));
        }
        var tabs = (TabControl)XamlReader.Parse(fragment.ToString());
        tabs.DataContext = viewModel;
        return tabs;
    }

    private static void DrainBindings() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void OnSta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Settings binding test timed out.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
