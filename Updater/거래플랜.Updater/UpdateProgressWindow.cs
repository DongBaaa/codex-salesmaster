using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
    private readonly WrapPanel _buttonPanel;
    private readonly Button _copyLogButton;
    private readonly Button _openLogFolderButton;
    private string _failureDetail = string.Empty;
    private string? _failureLogPath;

    public UpdateProgressWindow()
    {
        Title = "거래플랜 업데이트";
        Width = 560;
        MinWidth = 420;
        MinHeight = 220;
        MaxWidth = Math.Max(420, SystemParameters.WorkArea.Width - 48);
        MaxHeight = Math.Max(320, SystemParameters.WorkArea.Height - 48);
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Brushes.White;
        ShowInTaskbar = true;

        var panel = new Grid
        {
            Margin = new Thickness(24)
        };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _titleBlock = new TextBlock
        {
            Text = "업데이트 준비 중",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(25, 32, 56)),
            Margin = new Thickness(0, 0, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None
        };

        _detailBlock = new TextBlock
        {
            Text = "잠시만 기다려 주세요.",
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            Foreground = new SolidColorBrush(Color.FromRgb(90, 97, 120))
        };

        _detailScrollViewer = new ScrollViewer
        {
            Content = _detailBlock,
            MinHeight = 48,
            MaxHeight = Math.Max(120, Math.Min(360, SystemParameters.WorkArea.Height * 0.45)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 12)
        };

        _progressBar = new ProgressBar
        {
            Height = 10,
            Minimum = 0,
            Maximum = 100,
            IsIndeterminate = true,
            Foreground = new SolidColorBrush(Color.FromRgb(38, 95, 255)),
            Margin = new Thickness(0, 4, 0, 0)
        };

        _buttonPanel = new WrapPanel
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

        Grid.SetRow(_titleBlock, 0);
        Grid.SetRow(_detailScrollViewer, 1);
        Grid.SetRow(_progressBar, 2);
        Grid.SetRow(_buttonPanel, 3);
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

        MinHeight = 320;
        _titleBlock.Text = title;
        _titleBlock.Foreground = new SolidColorBrush(Color.FromRgb(176, 45, 49));
        _detailBlock.Text = detail + Environment.NewLine + Environment.NewLine + "아래 버튼으로 오류 로그를 복사하거나 로그 위치를 열어 전달할 수 있습니다.";
        _progressBar.IsIndeterminate = false;
        _progressBar.Value = 100;
        _progressBar.Foreground = new SolidColorBrush(Color.FromRgb(176, 45, 49));
        SetActionButtonText(_copyLogButton, HasReadableFailureLog() ? "로그 복사" : "오류 내용 복사");
        _openLogFolderButton.IsEnabled = HasReadableFailureLog();
        _buttonPanel.Visibility = Visibility.Visible;
    }

    private static Button CreateActionButton(string text)
        => new()
        {
            Content = new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                TextAlignment = TextAlignment.Center
            },
            MinWidth = 96,
            MinHeight = 38,
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(12, 6, 12, 6)
        };

    private static void SetActionButtonText(Button button, string text)
    {
        if (button.Content is TextBlock textBlock)
            textBlock.Text = text;
        else
            button.Content = text;
    }

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

            SetClipboardTextWithRetry(content.ToString());
            SetActionButtonText(_copyLogButton, "복사 완료");
        }
        catch (Exception ex)
        {
            _detailBlock.Text = _failureDetail + Environment.NewLine + Environment.NewLine + $"로그 복사에 실패했습니다: {ex.Message}";
        }
    }

    private static void SetClipboardTextWithRetry(string text)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!CanOpenClipboardForProbe())
                    throw new InvalidOperationException("Clipboard is busy.");

                Clipboard.SetText(text);
                return;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Thread.Sleep(100);
            }
        }

        throw lastError ?? new InvalidOperationException("Clipboard write failed.");
    }

    private static bool CanOpenClipboardForProbe()
    {
        if (!OpenClipboard(IntPtr.Zero))
            return false;

        CloseClipboard();
        return true;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

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
