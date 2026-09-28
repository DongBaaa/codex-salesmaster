using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

internal static class ItemAmountWritePolicy
{
    /// <summary>
    /// Keep protected prices when saving the non-monetary item fields. For a new
    /// item the entity's initial prices are zero until an authorized user prices it.
    /// Do not rewrite the DTO: exact replay receipts must identify the sent command.
    /// </summary>
    public static void Apply(Item entity, ItemDto dto, OfficeScopeService scope)
    {
        var purchase = entity.PurchasePrice;
        var sale = entity.SalePrice;
        var retail = entity.RetailPrice;
        var gradeA = entity.PriceGradeA;
        var gradeB = entity.PriceGradeB;
        var gradeC = entity.PriceGradeC;

        entity.Apply(dto);

        if (!scope.CanViewPurchaseAmounts())
            entity.PurchasePrice = purchase;
        if (!scope.CanViewSalesAmounts())
        {
            entity.SalePrice = sale;
            entity.RetailPrice = retail;
            entity.PriceGradeA = gradeA;
            entity.PriceGradeB = gradeB;
            entity.PriceGradeC = gradeC;
        }
    }
}
