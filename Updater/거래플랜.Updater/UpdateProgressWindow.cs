using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace 거래플랜.Updater;

internal sealed class UpdateProgressWindow : Window
{
    private readonly TextBlock _titleBlock;
    private readonly TextBlock _detailBlock;
    private readonly ScrollViewer _detailScrollViewer;
    private readonly ProgressBar _progressBar;
    private readonly StackPanel _buttonPanel;
    private readonly Button _copyLogButton;
    private readonly Button _openLogFolderButton;
    private string _failureDetail = string.Empty;
    private string? _failureLogPath;

    public UpdateProgressWindow()
    {
        Title = "거래플랜 업데이트";
        Width = 520;
        Height = 190;
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
            Foreground = new SolidColorBrush(Color.FromRgb(90, 97, 120))
        };

        _detailScrollViewer = new ScrollViewer
        {
            Content = _detailBlock,
            MaxHeight = 72,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
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

        _buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Visibility = Visibility.Collapsed
        };

        _copyLogButton = CreateActionButton("로그 복사");
        _copyLogButton.Click += (_, _) => CopyFailureLogToClipboard();

        _openLogFolderButton = CreateActionButton("로그 위치 열기");
        _openLogFolderButton.Click += (_, _) => OpenFailureLogFolder();

        var closeButton = CreateActionButton("닫기");
        closeButton.Click += (_, _) => Close();

        _buttonPanel.Children.Add(_copyLogButton);
        _buttonPanel.Children.Add(_openLogFolderButton);
        _buttonPanel.Children.Add(closeButton);

        panel.Children.Add(_titleBlock);
        panel.Children.Add(_detailScrollViewer);
        panel.Children.Add(_progressBar);
        panel.Children.Add(_buttonPanel);

        Content = panel;
    }

    public void SetStatus(string title, string detail)
    {
        _titleBlock.Text = title;
        _detailBlock.Text = detail;
    }

    public void ShowFailure(string title, string detail, string? logPath = null)
    {
        _failureDetail = detail;
        _failureLogPath = string.IsNullOrWhiteSpace(logPath) ? null : logPath;

        Height = 320;
        _detailScrollViewer.MaxHeight = 150;
        _titleBlock.Text = title;
        _titleBlock.Foreground = new SolidColorBrush(Color.FromRgb(176, 45, 49));
        _detailBlock.Text = detail + Environment.NewLine + Environment.NewLine + "아래 버튼으로 오류 로그를 복사하거나 로그 위치를 열어 전달할 수 있습니다.";
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 100;
        _progressBar.Foreground = new SolidColorBrush(Color.FromRgb(176, 45, 49));
        _copyLogButton.Content = HasReadableFailureLog() ? "로그 복사" : "오류 내용 복사";
        _openLogFolderButton.IsEnabled = HasReadableFailureLog();
        _buttonPanel.Visibility = Visibility.Visible;
    }

    private static Button CreateActionButton(string text)
        => new()
        {
            Content = text,
            MinWidth = 96,
            Height = 32,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 0, 12, 0)
        };

    private bool HasReadableFailureLog()
        => !string.IsNullOrWhiteSpace(_failureLogPath) && File.Exists(_failureLogPath);

    private void CopyFailureLogToClipboard()
    {
        try
        {
            var content = new StringBuilder();
            content.AppendLine("거래플랜 업데이트 실패");
            content.AppendLine();
            content.AppendLine(_failureDetail);

            if (HasReadableFailureLog())
            {
                content.AppendLine();
                content.AppendLine("--- update.log ---");
                content.AppendLine(File.ReadAllText(_failureLogPath!, Encoding.UTF8));
            }

            Clipboard.SetText(content.ToString());
            _copyLogButton.Content = "복사 완료";
        }
        catch (Exception ex)
        {
            _detailBlock.Text = _failureDetail + Environment.NewLine + Environment.NewLine + $"로그 복사에 실패했습니다: {ex.Message}";
        }
    }

    private void OpenFailureLogFolder()
    {
        if (!HasReadableFailureLog())
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select," + QuoteExplorerArgument(_failureLogPath!),
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _detailBlock.Text = _failureDetail + Environment.NewLine + Environment.NewLine + $"로그 위치를 열지 못했습니다: {ex.Message}";
        }
    }

    private static string QuoteExplorerArgument(string value)
        => '"' + value.Replace("\"", string.Empty) + '"';
}
