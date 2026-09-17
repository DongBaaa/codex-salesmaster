using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Infrastructure;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataGridTemplateColumnLayoutTests
{
    [Fact]
    public void ExplanationColumn_UsesAvailableSpaceAfterLoadRefreshAndResize()
        => OnSta(() =>
        {
            DataGridAutoColumnWidthService.RegisterGlobal();
            var rows = new ObservableCollection<Row> { new("거래처 연결과 청구 기준을 확인해야 하는 설명입니다.") };
            var grid = CreateGrid(rows);
            var description = AddTemplateColumn(grid, "무슨 문제인가요?", 1);
            var host = new Grid();
            host.Children.Add(grid);
            var window = new Window { Content = host, Width = 900, Height = 300, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show();
                Drain();
                Assert.True(description.ActualWidth > grid.ActualWidth * .65,
                    $"Description should fill remaining width: column={description.ActualWidth}, grid={grid.ActualWidth}");
                var initialWidth = description.ActualWidth;
                rows.Clear();
                Drain();
                rows.Add(new Row(new string('가', 100)));
                Drain();
                Assert.InRange(description.ActualWidth, initialWidth - 3, initialWidth + 3);
                window.Width += 250;
                Drain();
                Assert.True(description.ActualWidth > initialWidth + 200);
                window.Width -= 450;
                Drain();
                Assert.True(description.ActualWidth < initialWidth - 150);
                Assert.True(description.ActualWidth > grid.ActualWidth * .5);
                host.Children.Remove(grid);
                Drain();
                host.Children.Add(grid);
                Drain();
                Assert.True(description.ActualWidth > grid.ActualWidth * .5);
                Assert.Equal(DataGridLengthUnitType.Star, description.Width.UnitType);
            }
            finally { window.Close(); Drain(); }
        });

    [Fact]
    public void ProportionalTemplates_KeepRatiosBoundsAndExplicitUserResize()
        => OnSta(() =>
        {
            DataGridAutoColumnWidthService.RegisterGlobal();
            var rows = new ObservableCollection<Row> { new("거래처") };
            var grid = CreateGrid(rows);
            var first = AddTemplateColumn(grid, "거래처명", 2);
            var second = AddTemplateColumn(grid, "설명", 1);
            first.MinWidth = 190;
            second.MaxWidth = 350;
            var hidden = AddTemplateColumn(grid, "숨김", 3);
            hidden.Visibility = Visibility.Collapsed;
            var window = new Window { Content = grid, Width = 1000, Height = 300, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                window.Show(); Drain();
                Assert.True(first.ActualWidth + second.ActualWidth > grid.ActualWidth * .8);
                Assert.InRange(first.ActualWidth / second.ActualWidth, 1.9, 2.1);
                Assert.True(first.ActualWidth >= 190);
                Assert.True(second.ActualWidth <= 350);
                Assert.Equal(new DataGridLength(3, DataGridLengthUnitType.Star), hidden.Width);
                first.Width = new DataGridLength(260);
                rows.Add(new Row("새로고침")); Drain();
                Assert.Equal(DataGridLengthUnitType.Pixel, first.Width.UnitType);
                Assert.InRange(first.ActualWidth, 259, 261);
                Assert.True(second.ActualWidth <= 350);
            }
            finally { window.Close(); Drain(); }
        });

    private static DataGrid CreateGrid(ObservableCollection<Row> rows)
    {
        var grid = new DataGrid { ItemsSource = rows, AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column };
        grid.Columns.Add(new DataGridTextColumn { Header = "건수", Width = 70, Binding = new Binding(nameof(Row.Count)) });
        return grid;
    }

    private static DataGridTemplateColumn AddTemplateColumn(DataGrid grid, string header, double weight)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Description)));
        text.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
        var column = new DataGridTemplateColumn { Header = header, Width = new DataGridLength(weight, DataGridLengthUnitType.Star), CellTemplate = new DataTemplate { VisualTree = text } };
        grid.Columns.Add(column);
        return column;
    }

    private static void Drain()
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
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Layout validation timed out");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public sealed record Row(string Description, int Count = 1);
}
