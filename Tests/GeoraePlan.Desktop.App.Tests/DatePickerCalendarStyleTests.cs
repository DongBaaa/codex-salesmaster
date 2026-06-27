using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DatePickerCalendarStyleTests
{
    [Fact]
    public void AppDatePickerStyle_KeepsCalendarPopupWideAndResetsHeaderButtonWidth()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "App.xaml"));

        var calendarStyle = ExtractStyleBlock(
            xaml,
            "<Style x:Key=\"GeoraePlanDatePickerCalendarStyle\" TargetType=\"{x:Type Calendar}\">",
            "<Style TargetType=\"DatePicker\">",
            "DatePicker 달력 popup 기본 스타일을 찾을 수 없습니다.");
        var datePickerStyle = ExtractStyleBlock(
            xaml,
            "<Style TargetType=\"DatePicker\">",
            "<Style TargetType=\"{x:Type DatePickerTextBox}\">",
            "DatePicker 전역 스타일을 찾을 수 없습니다.");
        var calendarItemStyle = ExtractCalendarItemStyleBlock(xaml);

        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"320\"/>", calendarStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"280\"/>", calendarStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"CalendarStyle\" Value=\"{StaticResource GeoraePlanDatePickerCalendarStyle}\"/>", datePickerStyle, StringComparison.Ordinal);

        Assert.Contains("<Setter Property=\"Resources\">", calendarItemStyle, StringComparison.Ordinal);
        Assert.Contains("월/년도 헤더 버튼", calendarItemStyle, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"{x:Type Button}\">", calendarItemStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"0\"/>", calendarItemStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"0\"/>", calendarItemStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"HorizontalAlignment\" Value=\"Stretch\"/>", calendarItemStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"HorizontalContentAlignment\" Value=\"Center\"/>", calendarItemStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"Width\" Value=\"30\"/>", calendarItemStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"MaxWidth\" Value=\"30\"/>", calendarItemStyle, StringComparison.Ordinal);
    }

    private static string ExtractCalendarItemStyleBlock(string xaml)
        => ExtractStyleBlock(
            xaml,
            "<Style TargetType=\"{x:Type CalendarItem}\">",
            "<Style x:Key=\"UnifiedDatePickerButtonStyle\"",
            "CalendarItem 전역 스타일을 찾을 수 없습니다.",
            "CalendarItem 전역 스타일의 끝을 찾을 수 없습니다.");

    private static string ExtractStyleBlock(
        string xaml,
        string startMarker,
        string endMarker,
        string missingStartMessage,
        string? missingEndMessage = null)
    {
        var start = xaml.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, missingStartMessage);

        var end = xaml.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, missingEndMessage ?? $"{startMarker} 스타일의 끝을 찾을 수 없습니다.");

        return xaml[start..end];
    }

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
