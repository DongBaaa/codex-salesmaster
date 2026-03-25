using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace 거래플랜.Desktop.App.Services;

public static class PrintPreviewHelper
{
    private const double A4Width = 793.7;
    private const double A4Height = 1122.5;
    private const double DefaultPadding = 24;

    public static bool ShowPreviewAndPrint(FlowDocument document, string title, string jobName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ConfigureDocumentForA4(document);

        var previewWindow = new Window
        {
            Title = title,
            Width = 980,
            Height = 760,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.White
        };

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (owner is not null && owner != previewWindow)
            previewWindow.Owner = owner;

        var root = new Grid
        {
            Background = Brushes.White
        };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = new DockPanel
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A2B4A")),
            Margin = new Thickness(0, 0, 0, 1),
            LastChildFill = false
        };

        var description = new TextBlock
        {
            Text = "미리보기를 확인한 뒤 프린터를 선택해 인쇄하세요.",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            FontFamily = new FontFamily("맑은 고딕")
        };
        DockPanel.SetDock(description, Dock.Left);
        toolbar.Children.Add(description);

        var closeButton = new Button
        {
            Content = "닫기",
            Width = 90,
            Margin = new Thickness(8),
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#455A64")),
            Foreground = Brushes.White,
            BorderBrush = Brushes.Transparent,
            FontFamily = new FontFamily("맑은 고딕")
        };
        closeButton.Click += (_, _) => previewWindow.Close();
        DockPanel.SetDock(closeButton, Dock.Right);
        toolbar.Children.Add(closeButton);

        var printed = false;
        var isPrinting = false;
        var printButton = new Button
        {
            Content = "프린터 선택 후 인쇄",
            Width = 190,
            Margin = new Thickness(8),
            Padding = new Thickness(8, 4, 8, 4),
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E88E5")),
            Foreground = Brushes.White,
            BorderBrush = Brushes.Transparent,
            FontFamily = new FontFamily("맑은 고딕")
        };
        printButton.Click += (_, _) =>
        {
            if (isPrinting)
                return;

            try
            {
                isPrinting = true;
                printButton.IsEnabled = false;
                closeButton.IsEnabled = false;
                description.Text = "프린터 선택 창을 여는 중...";

                var dlg = new PrintDialog();
                if (dlg.ShowDialog() != true)
                {
                    description.Text = "인쇄를 취소했습니다.";
                    return;
                }

                ConfigureDocumentForA4(document);

                var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
                paginator.PageSize = new Size(A4Width, A4Height);
                dlg.PrintDocument(paginator, jobName);
                printed = true;
                description.Text = "인쇄를 완료했습니다.";
                previewWindow.Close();
            }
            catch (Exception ex)
            {
                description.Text = "인쇄 중 오류가 발생했습니다.";
                MessageBox.Show(
                    $"인쇄 중 오류가 발생했습니다.\n{ex.Message}",
                    "오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                if (previewWindow.IsVisible)
                {
                    isPrinting = false;
                    printButton.IsEnabled = true;
                    closeButton.IsEnabled = true;
                }
            }
        };
        DockPanel.SetDock(printButton, Dock.Right);
        toolbar.Children.Add(printButton);

        Grid.SetRow(toolbar, 0);
        root.Children.Add(toolbar);

        var viewer = new FlowDocumentScrollViewer
        {
            Document = document,
            Margin = new Thickness(10),
            Background = Brushes.White,
            Foreground = Brushes.Black,
            IsToolBarVisible = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        Grid.SetRow(viewer, 1);
        root.Children.Add(viewer);

        previewWindow.Content = root;
        previewWindow.ShowDialog();
        return printed;
    }

    private static void ConfigureDocumentForA4(FlowDocument document)
    {
        document.PageWidth = A4Width;
        document.PageHeight = A4Height;
        document.PagePadding = new Thickness(DefaultPadding);
        document.ColumnWidth = A4Width - (DefaultPadding * 2);
        document.ColumnGap = 0;
    }

}
