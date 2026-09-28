using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InvoiceScopeAndQuantityIntegrityTests
{
    [Theory]
    [MemberData(nameof(ItemAmountCases))]
    public async Task NullableItemPrices_NonMoneySaveAndReplayPreserveServerPrices(bool sync,bool create,int mask)
    {
        var user=ItemAmountUser(mask);await using var db=CreateDbContext(user);
        var item=CreateItem(ItemTrackingTypes.NonStock,0);
        item.PurchasePrice=701;item.SalePrice=1101;item.RetailPrice=1201;
        item.PriceGradeA=1001;item.PriceGradeB=901;item.PriceGradeC=801;
        if (!create) {db.Items.Add(item);await db.SaveChangesAsync();}
        var dto=item.ToDto();dto.ExpectedRevision=create ? 0 : item.Revision;
        dto.PurchasePrice=dto.SalePrice=dto.RetailPrice=dto.PriceGradeA=dto.PriceGradeB=dto.PriceGradeC=null;
        dto.SimpleMemo="가격 없는 비금액 수정";dto.MutationId=Guid.NewGuid().ToString("N");dto.MutationCreatedAtUtc=DateTime.UtcNow;
        dto.UpdatedAtUtc=DateTime.UtcNow.AddMinutes(1);
        var json=JsonSerializer.Serialize(dto);
        await SavePricedItem(sync,create,db,user,dto);db.ChangeTracker.Clear();
        var saved=await db.Items.SingleAsync(i=>i.Id==dto.Id);
        Assert.Equal(create ? 0m : 701m,saved.PurchasePrice);Assert.Equal(create ? 0m : 1101m,saved.SalePrice);
        Assert.Equal(create ? 0m : 1201m,saved.RetailPrice);Assert.Equal(create ? 0m : 1001m,saved.PriceGradeA);
        Assert.Equal(create ? 0m : 901m,saved.PriceGradeB);Assert.Equal(create ? 0m : 801m,saved.PriceGradeC);
        Assert.Equal(dto.SimpleMemo,saved.SimpleMemo);var revision=saved.Revision;
        await SavePricedItem(sync,create,db,user,JsonSerializer.Deserialize<ItemDto>(json)!,sync);
        db.ChangeTracker.Clear();Assert.Equal(revision,(await db.Items.SingleAsync(i=>i.Id==dto.Id)).Revision);
        Assert.Single(await db.ProcessedSyncMutations.ToListAsync());
    }

    [Fact]
    public void NullableGradeApply_PreservesExistingMoneyAndAllowsRealZero()
    {
        var grade=new ItemPriceGrade { UnitPrice=901m };
        grade.Apply(new ItemPriceGradeDto { UnitPrice=null,PriceGradeName="메모 아닌 등급명",IsActive=false });
        Assert.Equal(901m,grade.UnitPrice);Assert.False(grade.IsActive);
        grade.Apply(new ItemPriceGradeDto { UnitPrice=0m });Assert.Equal(0m,grade.UnitPrice);
    }
}
