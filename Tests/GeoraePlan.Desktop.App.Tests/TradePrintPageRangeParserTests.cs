using 거래플랜.Desktop.App.Printing;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class TradePrintPageRangeParserTests
{
    [Fact]
    public void TryParse_ParsesCommaAndRangeInInputOrder()
    {
        var parsed = TradePrintPageRangeParser.TryParse("1, 3, 5-7", 10, out var pages, out var errorMessage);

        Assert.True(parsed, errorMessage);
        Assert.Equal([1, 3, 5, 6, 7], pages);
    }

    [Fact]
    public void TryParse_RemovesDuplicatePagesWithoutReordering()
    {
        var parsed = TradePrintPageRangeParser.TryParse("2,1-3,2", 5, out var pages, out var errorMessage);

        Assert.True(parsed, errorMessage);
        Assert.Equal([2, 1, 3], pages);
    }

    [Fact]
    public void TryParse_RejectsPageBeyondDocumentPageCount()
    {
        var parsed = TradePrintPageRangeParser.TryParse("1,6", 5, out var pages, out var errorMessage);

        Assert.False(parsed);
        Assert.Empty(pages);
        Assert.Contains("총 5쪽", errorMessage);
    }

    [Fact]
    public void TryParse_RejectsReverseRange()
    {
        var parsed = TradePrintPageRangeParser.TryParse("5-3", 10, out var pages, out var errorMessage);

        Assert.False(parsed);
        Assert.Empty(pages);
        Assert.Contains("시작 번호", errorMessage);
    }
}
