using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DatePickerCalendarStyleTests
{
    [Fact]
    public void AppCalendarItemStyle_ResetsDatePickerButtonWidthInsideCalendarPopup()
    {
        var xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Desktop",
            "거래플랜.Desktop.App",
            "App.xaml"));

        var calendarItemStyle = ExtractCalendarItemStyleBlock(xaml);

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
    {
        const string startMarker = "<Style TargetType=\"{x:Type CalendarItem}\">";
        const string endMarker = "<Style x:Key=\"UnifiedDatePickerButtonStyle\"";

        var start = xaml.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, "CalendarItem 전역 스타일을 찾을 수 없습니다.");

        var end = xaml.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, "CalendarItem 전역 스타일의 끝을 찾을 수 없습니다.");

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
