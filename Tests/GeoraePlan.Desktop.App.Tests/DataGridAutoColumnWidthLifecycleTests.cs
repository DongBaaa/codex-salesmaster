using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Infrastructure;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataGridAutoColumnWidthLifecycleTests
{
    [Fact]
    public void GlobalAutoFit_HandlesInitialAndDynamicallyAddedGridsWithoutLocalLoadedHandlers()
        => OnSta(() =>
        {
            DataGridAutoColumnWidthService.RegisterGlobal();
            var host = new Grid();
            var first = CreateGrid(new ObservableCollection<Row> { new("첫 표") });
            host.Children.Add(first);
            WithWindow(host, () =>
            {
                Assert.True(first.IsLoaded);
                Assert.True(first.MinColumnWidth >= 48);
                Assert.True(first.Columns[0].MinWidth >= 220);
                Assert.True(first.Columns[1].MinWidth >= 140);
                host.Children.Remove(first);
                DrainDispatcher();
                var late = CreateGrid(new ObservableCollection<Row> { new("나중에 추가한 표") });
                host.Children.Add(late);
                DrainDispatcher();
                Assert.True(late.IsLoaded);
                Assert.True(late.MinColumnWidth >= 48);
                Assert.True(late.Columns[0].MinWidth >= 220);
            });
        });

    [Fact]
    public void GlobalAutoFit_RemeasuresCollectionChangesAndReconnectsAfterReload()
        => OnSta(() =>
        {
            DataGridAutoColumnWidthService.RegisterGlobal();
            var rows = new ObservableCollection<Row> { new("짧은 이름") };
            var grid = CreateGrid(rows);
            var host = new Grid();
            host.Children.Add(grid);
            WithWindow(host, () =>
            {
                var shortWidth = grid.Columns[0].ActualWidth;
                rows.Add(new Row(new string('가', 55)));
                DrainDispatcher();
                var longWidth = grid.Columns[0].ActualWidth;
                Assert.True(longWidth > shortWidth);
                host.Children.Remove(grid);
                DrainDispatcher();
                Assert.False(grid.IsLoaded);
                rows.Add(new Row(new string('나', 85)));
                DrainDispatcher();
                Assert.Equal(longWidth, grid.Columns[0].ActualWidth);
                host.Children.Add(grid);
                DrainDispatcher();
                Assert.True(grid.Columns[0].ActualWidth > longWidth);
                var reloadedWidth = grid.Columns[0].ActualWidth;
                rows.Add(new Row(new string('다', 100)));
                DrainDispatcher();
                Assert.True(grid.Columns[0].ActualWidth > reloadedWidth);
            });
        });

    [Fact]
    public void GlobalAutoFit_PreservesExplicitMinimumMaximumAndHiddenColumnWidths()
        => OnSta(() =>
        {
            DataGridAutoColumnWidthService.RegisterGlobal();
            var grid = CreateGrid(new ObservableCollection<Row> { new(new string('가', 55)) });
            grid.Columns[0].MaxWidth = 260;
            grid.Columns[1].MinWidth = 20;
            grid.Columns[1].MaxWidth = 60;
            var hidden = new DataGridTextColumn { Header = "숨김", Width = 88, Visibility = Visibility.Collapsed };
            grid.Columns.Add(hidden);
            WithWindow(grid, () =>
            {
                Assert.True(grid.MinColumnWidth >= 48);
                Assert.True(grid.EnableColumnVirtualization);
                Assert.Equal(220, grid.Columns[0].MinWidth);
                Assert.InRange(grid.Columns[0].ActualWidth, 220, 260);
                Assert.InRange(grid.Columns[1].MinWidth, 20, 60);
                Assert.True(grid.Columns[1].ActualWidth <= 60);
                Assert.Equal(88, hidden.Width.Value);
            });
        });

    private static DataGrid CreateGrid(ObservableCollection<Row> rows)
    {
        var grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = rows };
        grid.Columns.Add(new DataGridTextColumn { Header = "거래처", Binding = new Binding(nameof(Row.Name)), MinWidth = 220, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "비고", Width = 350 });
        return grid;
    }

    private static void WithWindow(UIElement content, Action assertions)
    {
        var window = new Window { Content = content, Width = 600, Height = 260, ShowActivated = false, ShowInTaskbar = false, Title = "표 자동 너비 회귀 검증" };
        try { window.Show(); DrainDispatcher(); assertions(); }
        finally { window.Close(); DrainDispatcher(); }
    }

    private static void DrainDispatcher()
    {
        for (var i = 0; i < 3; i++)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        }
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF lifecycle verification timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public sealed record Row(string Name);
}
