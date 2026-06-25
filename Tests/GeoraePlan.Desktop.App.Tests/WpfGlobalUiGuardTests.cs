using System.Text.RegularExpressions;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class WpfGlobalUiGuardTests
{
    [Fact]
    public void EveryViewDatePicker_KeepsCalendarPopupButtonStyleScopedToDatePickerButton()
    {
        var root = FindRepositoryRoot();
        var viewRoot = Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "Views");
        var failures = new List<string>();

        foreach (var xamlPath in Directory.EnumerateFiles(viewRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var xaml = File.ReadAllText(xamlPath);
            foreach (Match match in Regex.Matches(
                         xaml,
                         "<DatePicker\\b[\\s\\S]*?</DatePicker>|<DatePicker\\b[^>]*/>",
                         RegexOptions.CultureInvariant))
            {
                var datePickerBlock = match.Value;
                if (datePickerBlock.Contains("<DatePicker.Resources>", StringComparison.Ordinal) &&
                    datePickerBlock.Contains("TargetType=\"{x:Type Button}\"", StringComparison.Ordinal) &&
                    datePickerBlock.Contains("BasedOn=\"{StaticResource", StringComparison.Ordinal) &&
                    datePickerBlock.Contains("DatePickerButtonStyle", StringComparison.Ordinal))
                {
                    continue;
                }

                failures.Add($"{RelativeToRoot(root, xamlPath)}:{GetLineNumber(xaml, match.Index)} DatePicker 버튼 스타일 범위가 누락되었습니다.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void DatePickerStyles_KeepCalendarPopupWideEnoughAndDoNotLeakButtonWidthIntoCalendar()
    {
        var root = FindRepositoryRoot();
        var xamlFiles = Directory.EnumerateFiles(
                Path.Combine(root, "Desktop", "거래플랜.Desktop.App"),
                "*.xaml",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        var failures = new List<string>();
        foreach (var xamlPath in xamlFiles)
        {
            var xaml = File.ReadAllText(xamlPath);
            foreach (Match match in Regex.Matches(
                         xaml,
                         "<Style\\s+TargetType=\"(?:\\{x:Type\\s+)?DatePicker(?:\\})?\"[^>]*>[\\s\\S]*?</Style>",
                         RegexOptions.CultureInvariant))
            {
                var styleBlock = match.Value;
                if (styleBlock.Contains("BasedOn=", StringComparison.Ordinal))
                    continue;

                if (!styleBlock.Contains("CalendarStyle", StringComparison.Ordinal) ||
                    !TryReadSetterInt(styleBlock, "MinWidth", out var minWidth) ||
                    minWidth < 150 ||
                    !TryReadSetterInt(styleBlock, "Width", out var width) ||
                    width < 150 ||
                    !TryReadSetterInt(styleBlock, "MaxWidth", out var maxWidth) ||
                    maxWidth < 150)
                {
                    failures.Add($"{RelativeToRoot(root, xamlPath)}:{GetLineNumber(xaml, match.Index)} DatePicker 스타일의 달력 폭/본문 폭 기준이 부족합니다.");
                }
            }
        }

        var appXaml = File.ReadAllText(Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "App.xaml"));
        var calendarItemBlock = ExtractBlock(
            appXaml,
            "<Style TargetType=\"{x:Type CalendarItem}\">",
            "<Style x:Key=\"UnifiedDatePickerButtonStyle\"");

        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"320\"/>", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"280\"/>", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"{x:Type Button}\">", calendarItemBlock, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"0\"/>", calendarItemBlock, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"HorizontalAlignment\" Value=\"Stretch\"/>", calendarItemBlock, StringComparison.Ordinal);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void DataIntegrityAlertWindow_KeepsScrollableBodyAndWiresVisibleActionButtons()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "DataIntegrityAlertWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "DataIntegrityAlertWindow.xaml.cs"));

        Assert.Contains("MinWidth=\"920\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"560\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer Grid.Row=\"1\" VerticalScrollBarVisibility=\"Auto\">", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Description}\" TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SuggestedAction}\" TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"수정 화면 열기\" Click=\"FixButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"닫기(F12)\" Click=\"CloseButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("NonClosingActionRequested.Invoke", code, StringComparison.Ordinal);
        Assert.Contains("DialogWindowCloseHelper.Close(this)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryViewReferencedInteractionHandler_ExistsInCodeBehind()
    {
        var root = FindRepositoryRoot();
        var viewRoot = Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "Views");
        var failures = new List<string>();
        var eventPattern = new Regex(
            "\\b(?:Click|MouseDoubleClick|KeyDown|TextChanged|SelectionChanged|Checked|Unchecked|Loaded|Closing)=\"(?<handler>[A-Za-z_][A-Za-z0-9_]*)\"",
            RegexOptions.CultureInvariant);

        foreach (var xamlPath in Directory.EnumerateFiles(viewRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var xaml = File.ReadAllText(xamlPath);
            var codePath = xamlPath + ".cs";
            var code = File.Exists(codePath) ? File.ReadAllText(codePath) : string.Empty;
            foreach (Match match in eventPattern.Matches(xaml))
            {
                var handlerName = match.Groups["handler"].Value;
                if (code.Contains(handlerName, StringComparison.Ordinal))
                    continue;

                failures.Add($"{RelativeToRoot(root, xamlPath)}:{GetLineNumber(xaml, match.Index)} '{handlerName}' handler가 code-behind에 없습니다.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ImmediateActionSelectionCheckboxes_UpdateSourceOnFirstClick()
    {
        var root = FindRepositoryRoot();

        AssertImmediateSelectionCheckbox(
            root,
            "InvoiceHistoryWindow.xaml",
            "ConfirmButton_Click");
        AssertImmediateSelectionCheckbox(
            root,
            "RentalCustomerOnboardingWindow.xaml",
            "ApplySelectedAssetsToTemplateCommand");
    }

    [Fact]
    public void RentalAssetWindow_KeepsDetailSelectionSingleAndSelectionAutosaveStable()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "RentalAssetWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "RentalAssetViewModel.cs"));

        Assert.Contains("ItemsSource=\"{Binding Rows}\" SelectedItem=\"{Binding SelectedRow}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionMode=\"Single\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionUnit=\"FullRow\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "IsChecked=\"{Binding IsSelected, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionMode=\"Extended\"", xaml, StringComparison.Ordinal);
        Assert.Contains("private async Task<bool> HandleSelectionAutoSaveAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "preserveSelectionRowId: requestedSelection?.Source.Id,\n            refreshAfterSave: false",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "fullRow.IsSelected = current?.IsSelected ?? fullRow.IsSelected;",
            viewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PaymentTransferVerifier_CapturesRuntimeWindowScreenshotsAndDatePickerMetrics()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "tasks",
            "PaymentTransferVerifier",
            "Program.cs"));

        Assert.Contains("RenderTargetBitmap", source, StringComparison.Ordinal);
        Assert.Contains("PngBitmapEncoder", source, StringComparison.Ordinal);
        Assert.Contains("CaptureWindow(paymentAdvanceWindow", source, StringComparison.Ordinal);
        Assert.Contains("CollectDatePickerMetrics(paymentAdvanceWindow", source, StringComparison.Ordinal);
        Assert.Contains("ValidateDatePickerMetrics(datePickerMetrics)", source, StringComparison.Ordinal);
        Assert.Contains("DatePickerRuntimeMetric", source, StringComparison.Ordinal);
        Assert.Contains("WindowScreenshot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfDatePickerRuntimeAudit_CapturesRemainingDatePickerWindows()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "tasks",
            "WpfDatePickerRuntimeAudit",
            "Program.cs"));

        Assert.Contains("RequiredWindowDatePickerCounts", source, StringComparison.Ordinal);
        Assert.Contains("[\"customer-edit\"] = 3", source, StringComparison.Ordinal);
        Assert.Contains("[\"inventory-transfer\"] = 1", source, StringComparison.Ordinal);
        Assert.Contains("[\"period-ledger\"] = 2", source, StringComparison.Ordinal);
        Assert.Contains("[\"print-edit\"] = 1", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-asset-link\"] = 1", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-asset\"] = 5", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-assignment-history-edit\"] = 2", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-billing\"] = 2", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-contract-editor\"] = 3", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-customer-onboarding\"] = 1", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-equipment-replacement\"] = 1", source, StringComparison.Ordinal);
        Assert.Contains("[\"yeonsu-delivery\"] = 2", source, StringComparison.Ordinal);
        Assert.Contains("RenderTargetBitmap", source, StringComparison.Ordinal);
        Assert.Contains("PngBitmapEncoder", source, StringComparison.Ordinal);
        Assert.Contains("ValidateDatePickerMetrics(datePickerMetrics)", source, StringComparison.Ordinal);
        Assert.Contains("ComplementsExistingPaymentTransferVerifier = true", source, StringComparison.Ordinal);
    }

    private static bool TryReadSetterInt(string styleBlock, string propertyName, out int value)
    {
        var match = Regex.Match(
            styleBlock,
            $"Property=\"{Regex.Escape(propertyName)}\"\\s+Value=\"(?<value>\\d+)\"",
            RegexOptions.CultureInvariant);

        if (match.Success && int.TryParse(match.Groups["value"].Value, out value))
            return true;

        value = 0;
        return false;
    }

    private static string ExtractBlock(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"시작 마커를 찾을 수 없습니다: {startMarker}");

        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"끝 마커를 찾을 수 없습니다: {endMarker}");

        return source[start..end];
    }

    private static void AssertImmediateSelectionCheckbox(string root, string viewName, string actionMarker)
    {
        var xamlPath = Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "Views", viewName);
        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("DataGridCheckBoxColumn Header=\"선택\" Binding=\"{Binding IsSelected", xaml, StringComparison.Ordinal);
        Assert.Contains("<DataGridTemplateColumn Header=\"선택\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "IsChecked=\"{Binding IsSelected, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(actionMarker, xaml, StringComparison.Ordinal);
    }

    private static int GetLineNumber(string source, int index)
        => source[..index].Count(ch => ch == '\n') + 1;

    private static string RelativeToRoot(string root, string path)
        => Path.GetRelativePath(root, path);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "거래플랜.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜.sln을 찾을 수 없습니다.");
    }
}
