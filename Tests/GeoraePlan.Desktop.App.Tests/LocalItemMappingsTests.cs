using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalItemMappingsTests
{
    [Fact]
    public void ItemRoundTrip_PreservesOptionalCatalogFields()
    {
        var source = new LocalItem
        {
            Id = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Mapping roundtrip item",
            NameMatchKey = "MAPPINGROUNDTRIPITEM",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "BOX",
            BoxQuantity = 7.5m,
            StorageLocation = "C-02-04",
            LastPurchaseDate = new DateOnly(2026, 7, 10),
            LastSaleDate = new DateOnly(2026, 7, 18),
            CreatedAtUtc = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAtUtc = new DateTime(2026, 7, 18, 0, 0, 0, DateTimeKind.Utc),
            Revision = 42,
            IsDirty = true
        };

        var dto = LocalMappings.ToDto(source);
        var roundTripped = LocalMappings.ToLocal(dto);

        Assert.Equal(7.5m, dto.BoxQuantity);
        Assert.Equal("C-02-04", dto.StorageLocation);
        Assert.Equal(new DateOnly(2026, 7, 10), dto.LastPurchaseDate);
        Assert.True(dto.LastPurchaseDateSpecified);
        Assert.Equal(new DateOnly(2026, 7, 18), dto.LastSaleDate);
        Assert.True(dto.LastSaleDateSpecified);
        Assert.Equal(source.BoxQuantity, roundTripped.BoxQuantity);
        Assert.Equal(source.StorageLocation, roundTripped.StorageLocation);
        Assert.Equal(source.LastPurchaseDate, roundTripped.LastPurchaseDate);
        Assert.Equal(source.LastSaleDate, roundTripped.LastSaleDate);
    }
}
