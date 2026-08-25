using System.Windows;
using System.Windows.Controls;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.Views;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ProgrammaticWindowFullTextTests
{
    [Fact]
    public void PeriodLedgerMemoDialog_IsResizableScrollableAndUsesWrappingActions()
    {
        RunOnSta(() =>
        {
            var (dialog, input) = PeriodLedgerWindow.CreateMemoEditDialog(
                "매우 긴 전표메모 수정 제목",
                "매우 긴 전표메모 입력 안내를 생략하지 않고 표시합니다.",
                string.Join(' ', Enumerable.Repeat("기존 메모 전체 내용", 20)));
            try
            {
                Assert.Equal(ResizeMode.CanResizeWithGrip, dialog.ResizeMode);
                Assert.True(dialog.MinWidth >= 360);
                Assert.True(dialog.MinHeight >= 280);
                Assert.True(dialog.MaxWidth <= SystemParameters.WorkArea.Width);
                Assert.True(dialog.MaxHeight <= SystemParameters.WorkArea.Height);
                Assert.True(input.AcceptsReturn);
                Assert.Equal(TextWrapping.Wrap, input.TextWrapping);
                Assert.Equal(ScrollBarVisibility.Auto, input.VerticalScrollBarVisibility);

                var root = Assert.IsType<Grid>(dialog.Content);
                var caption = Assert.Single(root.Children.OfType<TextBlock>());
                Assert.Equal(TextWrapping.Wrap, caption.TextWrapping);
                Assert.Equal(TextTrimming.None, caption.TextTrimming);
                var actions = Assert.Single(root.Children.OfType<WrapPanel>());
                var buttons = actions.Children.OfType<Button>().ToArray();
                Assert.Equal(2, buttons.Length);
                Assert.All(buttons, button =>
                {
                    Assert.True(button.MinHeight >= 38);
                    Assert.True(double.IsNaN(button.Height));
                    var text = Assert.IsType<TextBlock>(button.Content);
                    Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
                    Assert.Equal(TextTrimming.None, text.TextTrimming);
                });
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    [Fact]
    public void PrintPreviewShell_KeepsLongGuidanceAndActionsOutsideDocumentViewport()
    {
        RunOnSta(() =>
        {
            var window = PrintPreviewHelper.CreatePreviewShell(
                "매우 긴 인쇄 미리보기 제목",
                out var root,
                out var description,
                out var actions);
            try
            {
                Assert.Equal(ResizeMode.CanResizeWithGrip, window.ResizeMode);
                Assert.True(window.MinWidth >= 520);
                Assert.True(window.MinHeight >= 420);
                Assert.Equal(3, root.RowDefinitions.Count);
                Assert.Equal(TextWrapping.Wrap, description.TextWrapping);
                Assert.Equal(TextTrimming.None, description.TextTrimming);
                Assert.Equal(1, Grid.GetRow(actions));
                Assert.Equal(HorizontalAlignment.Right, actions.HorizontalAlignment);

                var source = File.ReadAllText(Path.Combine(
                    FindRepositoryRoot(),
                    "Desktop",
                    "거래플랜.Desktop.App",
                    "Services",
                    "PrintPreviewHelper.cs"));
                Assert.Contains("Content = CreateWrappedButtonText(\"닫기\")", source, StringComparison.Ordinal);
                Assert.Contains("Content = CreateWrappedButtonText(\"프린터 선택 후 인쇄\")", source, StringComparison.Ordinal);
                Assert.DoesNotMatch("(?m)^\\s*Width = (?:90|190),", source);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ActivityPopup_ExplicitlyKeepsFullTextContractWhenResponsiveSizingIsDisabled()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "App.xaml.cs"));

        Assert.Contains("ResponsiveWindowBehavior.SetIsEnabled(popup, false);", source, StringComparison.Ordinal);
        Assert.Contains("FullTextLayoutBehavior.SetIsEnabled(popup, true);", source, StringComparison.Ordinal);
        Assert.Contains("TextWrapping = TextWrapping.Wrap", source, StringComparison.Ordinal);
        Assert.Contains("TextTrimming = TextTrimming.None", source, StringComparison.Ordinal);
    }

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
                Directory.Exists(Path.Combine(current.FullName, "Tests")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root could not be located.");
    }
}
