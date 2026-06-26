using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InvoiceOfficeWarehouseSelectionPolicyTests
{
    [Fact]
    public void ResolveSelectableOfficeCode_FallsBackWhenRequestedOfficeIsNotWritable()
    {
        var offices = new[]
        {
            new LocalOffice { Code = OfficeCodeCatalog.Usenet, Name = "USENET" }
        };

        var resolved = InvoiceOfficeWarehouseSelectionPolicy.ResolveSelectableOfficeCode(
            OfficeCodeCatalog.Yeonsu,
            offices,
            OfficeCodeCatalog.Usenet);

        Assert.Equal(OfficeCodeCatalog.Usenet, resolved);
    }

    [Fact]
    public void FilterWarehousesForOffice_OnlyReturnsSelectedResponsibleOfficeWarehouses()
    {
        var warehouses = CreateWarehouses();

        var filtered = InvoiceOfficeWarehouseSelectionPolicy.FilterWarehousesForOffice(
            warehouses,
            OfficeCodeCatalog.Yeonsu);

        var warehouse = Assert.Single(filtered);
        Assert.Equal(OfficeCodeCatalog.YeonsuMainWarehouse, warehouse.Code);
    }

    [Fact]
    public void ResolveWarehouseCode_ReplacesCrossOfficeWarehouseWithResponsibleOfficeDefault()
    {
        var warehouses = CreateWarehouses();

        var resolved = InvoiceOfficeWarehouseSelectionPolicy.ResolveWarehouseCode(
            OfficeCodeCatalog.YeonsuMainWarehouse,
            OfficeCodeCatalog.Usenet,
            warehouses);

        Assert.Equal(OfficeCodeCatalog.UsenetMainWarehouse, resolved);
    }

    private static IReadOnlyList<LocalWarehouse> CreateWarehouses()
        =>
        [
            new LocalWarehouse
            {
                OfficeCode = OfficeCodeCatalog.Usenet,
                Code = OfficeCodeCatalog.UsenetMainWarehouse,
                Name = "USENET main",
                IsActive = true
            },
            new LocalWarehouse
            {
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                Code = OfficeCodeCatalog.YeonsuMainWarehouse,
                Name = "YEONSU main",
                IsActive = true
            }
        ];
}
