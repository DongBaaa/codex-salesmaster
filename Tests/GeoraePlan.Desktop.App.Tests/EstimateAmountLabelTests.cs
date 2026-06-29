using System.Reflection;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class EstimateAmountLabelTests
{
    [Theory]
    [InlineData(0, "일금 영 원정   ( ₩0 )")]
    [InlineData(1, "일금 일 원정   ( ₩1 )")]
    [InlineData(10, "일금 일십 원정   ( ₩10 )")]
    [InlineData(100, "일금 일백 원정   ( ₩100 )")]
    [InlineData(1000, "일금 일천 원정   ( ₩1,000 )")]
    [InlineData(1000000, "일금 일백만 원정   ( ₩1,000,000 )")]
    [InlineData(2970000, "일금 이백구십칠만 원정   ( ₩2,970,000 )")]
    [InlineData(123456789, "일금 일억이천삼백사십오만육천칠백팔십구 원정   ( ₩123,456,789 )")]
    public void BuildAmountLabel_UsesKoreanWonTextForEstimateTotal(decimal amount, string expected)
    {
        var method = typeof(SupplementDocumentBuilder).GetMethod(
            "BuildAmountLabel",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(method);
        var actual = Assert.IsType<string>(method!.Invoke(null, [amount]));

        Assert.Equal(expected, actual);
    }
}
