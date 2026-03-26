using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace 거래플랜.Updater;

internal sealed class UpdateProgressWindow : Window
{
    private readonly TextBlock _titleBlock;
    private readonly TextBlock _detailBlock;
    private readonly ProgressBar _progressBar;

    public UpdateProgressWindow()
    {
        Title = "거래플랜 업데이트";
        Width = 460;
        Height = 170;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Background = Brushes.White;
        ShowInTaskbar = true;

        var panel = new StackPanel
        {
            Margin = new Thickness(24),
            Orientation = Orientation.Vertical
        };

        _titleBlock = new TextBlock
        {
            Text = "업데이트 준비 중",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(25, 32, 56)),
            Margin = new Thickness(0, 0, 0, 8)
        };

        _detailBlock = new TextBlock
        {
            Text = "잠시만 기다려 주세요.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(90, 97, 120)),
            Margin = new Thickness(0, 0, 0, 16)
        };

        _progressBar = new ProgressBar
        {
            Height = 10,
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true,
            Foreground = new SolidColorBrush(Color.FromRgb(38, 95, 255))
        };

        panel.Children.Add(_titleBlock);
        panel.Children.Add(_detailBlock);
        panel.Children.Add(_progressBar);

        Content = panel;
    }

    public void SetStatus(string title, string detail)
    {
        _titleBlock.Text = title;
        _detailBlock.Text = detail;
    }

    public void ShowFailure(string title, string detail)
    {
        _titleBlock.Text = title;
        _titleBlock.Foreground = new SolidColorBrush(Color.FromRgb(176, 45, 49));
        _detailBlock.Text = detail;
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 100;
        _progressBar.Foreground = new SolidColorBrush(Color.FromRgb(176, 45, 49));
    }
}
