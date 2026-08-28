using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Infrastructure;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class FullTextUiContractTests
{
    [Fact]
    public void DesktopAndUpdaterSources_HaveNoEllipsisNoWrapOrFixedDataGridRows()
    {
        var root = FindRepositoryRoot();
        var sourceRoots = new[]
        {
            Path.Combine(root, "Desktop"),
            Path.Combine(root, "Updater")
        };
        var failures = new List<string>();

        foreach (var sourceRoot in sourceRoots)
        {
            foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.*", SearchOption.AllDirectories)
                         .Where(static path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                                               path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                         .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                                               !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                                               !path.Contains($"{Path.DirectorySeparatorChar}Backup{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
            {
                var source = File.ReadAllText(path);
                AddFailureIfPresent(failures, root, path, source, "CharacterEllipsis");
                AddFailureIfPresent(failures, root, path, source, "WordEllipsis");
                AddFailureIfPresent(failures, root, path, source, "TextWrapping=\"NoWrap\"");

                foreach (Match match in Regex.Matches(
                             source,
                             "(?<!Min)(?:RowHeight|ColumnHeaderHeight)=\"[0-9]+(?:\\.[0-9]+)?\"|<Setter\\s+Property=\"(?:RowHeight|ColumnHeaderHeight)\"\\s+Value=\"[0-9]+(?:\\.[0-9]+)?\"",
                             RegexOptions.CultureInvariant))
                {
                    failures.Add($"{Path.GetRelativePath(root, path)}:{LineNumber(source, match.Index)} fixed grid row: {match.Value}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void GlobalResourcesAndDynamicWindows_EnforceFullTextLayout()
    {
        var root = FindRepositoryRoot();
        var appXaml = File.ReadAllText(Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "App.xaml"));
        var designSystem = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Themes",
            "TradePlanDesignSystem.xaml"));
        var behavior = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Infrastructure",
            "FullTextLayoutBehavior.cs"));

        Assert.Contains("infra:FullTextLayoutBehavior.IsEnabled", appXaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping\" Value=\"Wrap", appXaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming\" Value=\"None", appXaml, StringComparison.Ordinal);
        Assert.Contains("FullTextCenterContentTemplateSelector", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"ContentTemplateSelector\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"RowHeight\" Value=\"NaN\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"ColumnHeaderHeight\" Value=\"NaN\"", appXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Property=\"RowHeight\" Value=\"Auto\"", appXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Property=\"ColumnHeaderHeight\" Value=\"Auto\"", appXaml, StringComparison.Ordinal);
        var invalidDoubleAutoFiles = Directory
            .EnumerateFiles(
                Path.Combine(root, "Desktop", "거래플랜.Desktop.App"),
                "*.xaml",
                SearchOption.AllDirectories)
            .Where(path =>
            {
                var source = File.ReadAllText(path);
                return source.Contains("RowHeight=\"Auto\"", StringComparison.Ordinal) ||
                       source.Contains("ColumnHeaderHeight=\"Auto\"", StringComparison.Ordinal) ||
                       source.Contains("<Setter Property=\"RowHeight\" Value=\"Auto\"", StringComparison.Ordinal) ||
                       source.Contains("<Setter Property=\"ColumnHeaderHeight\" Value=\"Auto\"", StringComparison.Ordinal) ||
                       source.Contains("<Setter Property=\"Height\" Value=\"Auto\"", StringComparison.Ordinal);
            })
            .ToArray();
        Assert.Empty(invalidDoubleAutoFiles);
        Assert.Contains("Content=\"{TemplateBinding Content}\"", designSystem, StringComparison.Ordinal);
        Assert.Contains("ContentTemplateSelector=\"{TemplateBinding ContentTemplateSelector}\"", designSystem, StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"{DynamicResource FullTextCenterContentTemplateSelector}\"",
            designSystem,
            StringComparison.Ordinal);
        Assert.Contains("FrameworkElement.LoadedEvent", behavior, StringComparison.Ordinal);
        Assert.Contains("TextTrimming.None", behavior, StringComparison.Ordinal);
        Assert.Contains("TextWrapping.Wrap", behavior, StringComparison.Ordinal);
        Assert.Contains("DataGrid.RowHeightProperty", behavior, StringComparison.Ordinal);
    }

    [Fact]
    public void FullTextLayoutBehavior_RepairsCodeCreatedWindowVisuals()
    {
        RunOnSta(() =>
        {
            var text = new TextBlock
            {
                Text = "매우 긴 상태 안내 문구",
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            var button = new Button
            {
                Content = "아주 긴 업무 처리 버튼",
                Height = 24
            };
            var dataGrid = new DataGrid
            {
                RowHeight = 22,
                ColumnHeaderHeight = 22
            };
            var compactText = new TextBlock
            {
                Text = "한 줄로 유지할 조밀한 표 셀",
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.None
            };
            var compactRegion = new Border { Child = compactText };
            FullTextLayoutBehavior.SetPreserveSingleLine(compactRegion, true);
            var compactDataGrid = new DataGrid
            {
                RowHeight = 22,
                MinRowHeight = 0
            };
            FullTextLayoutBehavior.SetPreserveSingleLine(compactDataGrid, true);
            var panel = new StackPanel();
            panel.Children.Add(text);
            panel.Children.Add(button);
            panel.Children.Add(dataGrid);
            panel.Children.Add(compactRegion);
            panel.Children.Add(compactDataGrid);
            var window = new Window
            {
                Content = panel,
                Width = 520,
                Height = 360,
                ShowInTaskbar = false
            };

            try
            {
                FullTextLayoutBehavior.SetIsEnabled(window, true);
                window.Show();
                window.Dispatcher.Invoke(static () => { }, DispatcherPriority.ApplicationIdle);

                Assert.True(
                    text.TextWrapping == TextWrapping.Wrap,
                    $"Initial text wrapping remained {text.TextWrapping}.");
                Assert.True(
                    text.TextTrimming == TextTrimming.None,
                    $"Initial text trimming remained {text.TextTrimming}.");
                Assert.True(double.IsNaN(button.Height));
                Assert.True(button.MinHeight >= 38);
                Assert.True(double.IsNaN(dataGrid.RowHeight));
                Assert.True(double.IsNaN(dataGrid.ColumnHeaderHeight));
                Assert.True(dataGrid.MinRowHeight >= 32);
                Assert.Equal(TextWrapping.NoWrap, compactText.TextWrapping);
                Assert.Equal(TextTrimming.None, compactText.TextTrimming);
                Assert.Equal(22d, compactDataGrid.RowHeight);
                Assert.Equal(0d, compactDataGrid.MinRowHeight);

            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void AddFailureIfPresent(
        ICollection<string> failures,
        string root,
        string path,
        string source,
        string token)
    {
        var index = source.IndexOf(token, StringComparison.Ordinal);
        if (index >= 0)
            failures.Add($"{Path.GetRelativePath(root, path)}:{LineNumber(source, index)} forbidden token: {token}");
    }

    private static int LineNumber(string source, int index)
        => source.AsSpan(0, index).Count('\n') + 1;

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            throw failure;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Updater")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
