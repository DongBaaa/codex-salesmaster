using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class ItemWarehouseStockMutationReceiptTests
{
    [Fact]
    public void Create_UsesStableVersionedCanonicalHash()
    {
        var identity =
            ItemWarehouseStockMutationReceipt.Create(
                new ItemWarehouseStockDto
                {
                    ItemId = Guid.Parse(
                        "11111111-2222-3333-4444-555555555555"),
                    WarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 12.50m,
                    UpdatedAtUtc = new DateTime(
                        2026,
                        7,
                        30,
                        1,
                        2,
                        3,
                        456,
                        DateTimeKind.Utc),
                    Revision = 42,
                    ExpectedRevision = 40
                },
                " device-A ");

        const string expectedHash =
            "3e2656cd35e8701f97b10749f3576e04829765bcb9e02a50511fb757fcf6288e";
        Assert.Equal(
            expectedHash,
            identity.PayloadHash);
        Assert.Equal(
            $"server-receipt:item-warehouse-stock:v1:{expectedHash}",
            identity.MutationId);
        Assert.Equal(
            "device-A",
            identity.DeviceId);
        Assert.Equal(
            "11111111-2222-3333-4444-555555555555|USENET_MAIN",
            identity.EntityId);
        Assert.Equal(40, identity.ExpectedRevision);
    }

    [Fact]
    public void Create_NormalizesEquivalentValuesAndMissingTimestamp()
    {
        var itemId = Guid.Parse(
            "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var utc = new DateTime(
            2026,
            7,
            30,
            4,
            5,
            6,
            DateTimeKind.Utc);
        var unspecified =
            DateTime.SpecifyKind(
                utc,
                DateTimeKind.Unspecified);

        var first =
            ItemWarehouseStockMutationReceipt.Create(
                new ItemWarehouseStockDto
                {
                    ItemId = itemId,
                    WarehouseCode = " usenet_main ",
                    Quantity = 5.00m,
                    UpdatedAtUtc = utc,
                    Revision = 7,
                    ExpectedRevision = 7
                },
                "device-equivalent");
        var second =
            ItemWarehouseStockMutationReceipt.Create(
                new ItemWarehouseStockDto
                {
                    ItemId = itemId,
                    WarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 5m,
                    UpdatedAtUtc = unspecified,
                    Revision = 7,
                    ExpectedRevision = 7
                },
                " device-equivalent ");
        var missingTimestamp =
            new ItemWarehouseStockDto
            {
                ItemId = itemId,
                WarehouseCode =
                    OfficeCodeCatalog.UsenetMainWarehouse,
                Quantity = 5m
            };

        Assert.Equal(
            first,
            second);
        Assert.Equal(
            DateTime.UnixEpoch,
            missingTimestamp.UpdatedAtUtc);
        Assert.Equal(
            ItemWarehouseStockMutationReceipt.Create(
                missingTimestamp,
                "device-equivalent"),
            ItemWarehouseStockMutationReceipt.Create(
                new ItemWarehouseStockDto
                {
                    ItemId = itemId,
                    WarehouseCode =
                        OfficeCodeCatalog.UsenetMainWarehouse,
                    Quantity = 5m,
                    UpdatedAtUtc = default
                },
                "device-equivalent"));
    }

    [Fact]
    public void IsReservedMutationId_RecognizesCanonicalPrefix()
    {
        Assert.True(
            ItemWarehouseStockMutationReceipt
                .IsReservedMutationId(
                    " SERVER-RECEIPT:ITEM-WAREHOUSE-STOCK:V1:abc "));
        Assert.False(
            ItemWarehouseStockMutationReceipt
                .IsReservedMutationId(
                    "client:item-warehouse-stock:v1:abc"));
    }
}
