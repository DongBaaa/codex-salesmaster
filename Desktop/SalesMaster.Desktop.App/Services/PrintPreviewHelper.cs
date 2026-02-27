using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SalesMaster.Desktop.App.Services;

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
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#151F2E"))
        };

        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (owner is not null && owner != previewWindow)
            previewWindow.Owner = owner;

        var root = new Grid();
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
            try
            {
                var dlg = new PrintDialog();
                if (dlg.ShowDialog() != true)
                    return;

                ConfigureDocumentForA4(document);

                var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
                paginator.PageSize = new Size(A4Width, A4Height);

                var printableSize = new Size(
                    Math.Max(1, dlg.PrintableAreaWidth),
                    Math.Max(1, dlg.PrintableAreaHeight));

                var toPrint = (DocumentPaginator)new ScalingDocumentPaginator(
                    paginator,
                    new Size(A4Width, A4Height),
                    printableSize);

                dlg.PrintDocument(toPrint, jobName);
                printed = true;
                previewWindow.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"인쇄 중 오류가 발생했습니다.\n{ex.Message}",
                    "오류",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
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

    private sealed class ScalingDocumentPaginator : DocumentPaginator
    {
        private readonly DocumentPaginator _source;
        private readonly Size _targetPageSize;
        private readonly double _scale;
        private readonly double _offsetX;
        private readonly double _offsetY;

        public ScalingDocumentPaginator(DocumentPaginator source, Size sourcePageSize, Size targetPageSize)
        {
            _source = source;
            _targetPageSize = targetPageSize;

            var sx = targetPageSize.Width / sourcePageSize.Width;
            var sy = targetPageSize.Height / sourcePageSize.Height;
            _scale = Math.Min(1d, Math.Min(sx, sy));

            _offsetX = (targetPageSize.Width - (sourcePageSize.Width * _scale)) / 2d;
            _offsetY = (targetPageSize.Height - (sourcePageSize.Height * _scale)) / 2d;
        }

        public override bool IsPageCountValid => _source.IsPageCountValid;
        public override int PageCount => _source.PageCount;
        public override IDocumentPaginatorSource Source => _source.Source;

        public override Size PageSize
        {
            get => _targetPageSize;
            set => _source.PageSize = value;
        }

        public override DocumentPage GetPage(int pageNumber)
        {
            var page = _source.GetPage(pageNumber);
            var sourceContent = page.ContentBox.IsEmpty
                ? new Rect(new Point(0, 0), page.Size)
                : page.ContentBox;
            var sourceBleed = page.BleedBox.IsEmpty
                ? new Rect(new Point(0, 0), page.Size)
                : page.BleedBox;

            var container = new ContainerVisual();
            container.Children.Add(page.Visual);
            container.Transform = new MatrixTransform(
                _scale, 0,
                0, _scale,
                _offsetX, _offsetY);

            var contentBox = new Rect(
                _offsetX + (sourceContent.X * _scale),
                _offsetY + (sourceContent.Y * _scale),
                sourceContent.Width * _scale,
                sourceContent.Height * _scale);

            var bleedBox = new Rect(
                _offsetX + (sourceBleed.X * _scale),
                _offsetY + (sourceBleed.Y * _scale),
                sourceBleed.Width * _scale,
                sourceBleed.Height * _scale);

            return new DocumentPage(container, _targetPageSize, bleedBox, contentBox);
        }
    }
}
