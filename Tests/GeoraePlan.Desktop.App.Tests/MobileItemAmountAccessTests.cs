using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobileItemAmountAccessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RestrictedPriceEdit_PreservesHiddenPricesAndNonMoney(bool purchase, bool sales)
    {
        var source = Item();
        var edited = new ItemDto { SimpleMemo = "새 메모", CurrentStock = 9 };
        var access = Access(purchase, sales);
        access.ApplyEditedPrices(edited, source, 7, 8, 9);
        Assert.Equal(purchase ? 7 : 110, edited.PurchasePrice);
        Assert.Equal(sales ? 8 : 220, edited.SalePrice);
        Assert.Equal(sales ? 9 : 330, edited.RetailPrice);
        Assert.Equal(440, edited.PriceGradeA);
        Assert.Equal(550, edited.PriceGradeB);
        Assert.Equal(660, edited.PriceGradeC);
        Assert.Equal("새 메모", edited.SimpleMemo);
        Assert.Equal(9, edited.CurrentStock);
        Assert.Equal(110, source.PurchasePrice);
        var summary = access.Summary(source);
        Assert.Equal(purchase, summary.Contains("110"));
        Assert.Equal(sales, summary.Contains("220"));
        Assert.Equal(sales, summary.Contains("330"));
    }

    [Theory]
    [InlineData(false, false, "기본 단가 비공개")]
    [InlineData(false, true, "기본 단가 미등록")]
    [InlineData(true, false, "기본 단가 110원")]
    [InlineData(true, true, "기본 단가 110원")]
    public void ListFallback_DoesNotDisclosePurchaseToSalesOnly(bool purchase, bool sales, string expected)
    {
        var item = Item(); item.SalePrice = item.RetailPrice = 0;
        Assert.Equal(expected, Access(purchase, sales).ListSummary(item));
    }

    [Fact]
    public void PurchaseOnly_DoesNotUseHiddenSalesFallback()
        => Assert.Equal("기본 단가 110원", Access(true, false).ListSummary(Item()));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoEditPermission_PreservesPricesEvenWithVisibility(bool hasSource)
    {
        var dto = Item();
        new MobileItemAmountAccess(true, true, false).ApplyEditedPrices(dto, hasSource ? Item() : null, 1, 2, 3);
        Assert.Equal(hasSource ? 110 : 0, dto.PurchasePrice);
        Assert.Equal(hasSource ? 220 : 0, dto.SalePrice);
        Assert.Equal(hasSource ? 330 : 0, dto.RetailPrice);
        Assert.Equal(hasSource ? 660 : 0, dto.PriceGradeC);
    }

    [Fact]
    public void RevokedPermission_DiscardsEditedMoneyButPreservesMemo()
    {
        var dto = Item();
        Access(true, true).ApplyEditedPrices(dto, Item(), 1, 2, 3);
        dto.SimpleMemo = "권한 회수 후 저장";
        Access(false, false).ApplyEditedPrices(dto, Item(), 1, 2, 3);
        Assert.Equal(110, dto.PurchasePrice);
        Assert.Equal(220, dto.SalePrice);
        Assert.Equal("권한 회수 후 저장", dto.SimpleMemo);
    }

    [Fact]
    public void KnownZero_IsNotHidden()
    {
        Assert.DoesNotContain("비공개", Access(true, true).Summary(new ItemDto()));
        Assert.Contains("비공개", Access(false, false).Summary(new ItemDto()));
        var dto = Item(); Access(true, true).ApplyEditedPrices(dto, Item(), 0, 0, 0);
        Assert.Equal(0, dto.PurchasePrice); Assert.Equal(0, dto.SalePrice);
    }

    [Theory]
    [InlineData(false, "Admin", true, false)]
    [InlineData(true, "Admin", false, true)]
    [InlineData(true, "User", false, false)]
    [InlineData(true, "User", true, true)]
    public void Access_RequiresAuthenticationAndExplicitGrants(bool authenticated, string role, bool granted, bool expected)
    {
        var access = MobileItemAmountAccess.Capture(authenticated, role,
            granted ? ["amount.viewpurchase", "Amount.ViewSales"] : [], true);
        Assert.Equal(expected, access.Purchase); Assert.Equal(expected, access.Sales);
        Assert.Equal(authenticated, access.Edit);
    }

    private static MobileItemAmountAccess Access(bool purchase, bool sales)
        => MobileItemAmountAccess.Capture(true, "User",
            new[] { purchase ? "Amount.ViewPurchase" : "", sales ? "Amount.ViewSales" : "" }, true);
    private static ItemDto Item() => new() { PurchasePrice = 110, SalePrice = 220, RetailPrice = 330,
        PriceGradeA = 440, PriceGradeB = 550, PriceGradeC = 660 };
}
