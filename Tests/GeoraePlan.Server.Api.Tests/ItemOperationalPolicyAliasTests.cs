using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class ItemOperationalPolicyAliasTests
{
    [Theory]
    [InlineData("재고")]
    [InlineData("Stock")]
    [InlineData("Inventory")]
    public void ItemTrackingTypes_Normalize_MapsStockAliases(string value)
        => Assert.Equal(ItemTrackingTypes.Stock, ItemTrackingTypes.Normalize(value));

    [Theory]
    [InlineData("비재고")]
    [InlineData("NonStock")]
    [InlineData("Non-Stock")]
    [InlineData("Billing")]
    [InlineData("Service")]
    public void ItemTrackingTypes_Normalize_MapsNonStockAliases(string value)
        => Assert.Equal(ItemTrackingTypes.NonStock, ItemTrackingTypes.Normalize(value));

    [Theory]
    [InlineData("일반상품")]
    [InlineData("Product")]
    [InlineData("Goods")]
    public void ItemKinds_Normalize_MapsProductAliases(string value)
        => Assert.Equal(ItemKinds.Product, ItemKinds.Normalize(value));
}
