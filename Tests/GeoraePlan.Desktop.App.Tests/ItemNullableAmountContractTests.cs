using System.Reflection;
using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ItemNullableAmountContractTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task EditorWithoutHiddenFlags_CannotReplaceUnknownPricesWithZero(bool admin)
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var item=new LocalItem {Id=Guid.NewGuid(),NameOriginal="비공개 품목",NameMatchKey="비공개품목",
            OfficeCode=OfficeCodeCatalog.Usenet,TrackingType=ItemTrackingTypes.NonStock,
            PurchaseAmountsHidden=true,SalesAmountsHidden=true,Revision=1};
        db.Items.Add(item);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var session=new SessionState();session.SetSession("isolated",new UserSessionDto {UserId=Guid.NewGuid(),Username="nullable-editor",
            Role=admin ? DomainConstants.RoleAdmin : DomainConstants.RoleUser,OfficeCode=OfficeCodeCatalog.Usenet,
            TenantCode=TenantScopeCatalog.UsenetGroup,ScopeType=TenantScopeCatalog.ScopeOfficeOnly,
            Permissions=[AppPermissionNames.ItemEdit]});
        var service=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),session);
        var candidate=new LocalItem {Id=item.Id,NameOriginal=item.NameOriginal,NameMatchKey=item.NameMatchKey,
            OfficeCode=item.OfficeCode,TrackingType=item.TrackingType,Revision=1,SimpleMemo="메모 수정",PurchasePrice=0,SalePrice=0};
        await service.SaveInventoryItemAsync(candidate,session,OfficeCodeCatalog.Usenet,null);
        db.ChangeTracker.Clear();var saved=await db.Items.SingleAsync();
        Assert.Equal("메모 수정",saved.SimpleMemo);Assert.True(saved.PurchaseAmountsHidden);Assert.True(saved.SalesAmountsHidden);
        Assert.Null(LocalMappings.ToDto(saved).PurchasePrice);Assert.Null(LocalMappings.ToDto(saved).SalePrice);Assert.True(saved.IsDirty);
    }

    [Fact]
    public async Task GradeEditor_PreservesHiddenRowWhenValueOrRowIsMissing()
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var item=new LocalItem {Id=Guid.NewGuid()};var option=new LocalPriceGradeOption {Id=Guid.NewGuid(),Name="보존등급"};
        var grade=new LocalItemPriceGrade {Id=Guid.NewGuid(),ItemId=item.Id,PriceGradeOptionId=option.Id,PriceGradeName=option.Name,AmountsHidden=true};
        db.AddRange(item,option,grade);await db.SaveChangesAsync();
        var service=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),new SessionState());
        await service.SaveItemPriceGradesForItemAsync(item.Id,[new LocalItemPriceGrade {Id=grade.Id,PriceGradeOptionId=option.Id,UnitPrice=0}]);
        db.ChangeTracker.Clear();grade=await db.ItemPriceGrades.SingleAsync();Assert.True(grade.AmountsHidden);Assert.Null(LocalMappings.ToDto(grade).UnitPrice);
        await service.SaveItemPriceGradesForItemAsync(item.Id,[]);db.ChangeTracker.Clear();grade=await db.ItemPriceGrades.SingleAsync();Assert.False(grade.IsDeleted);
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, true, false)]
    [InlineData(true, false, false)] [InlineData(true, true, false)]
    [InlineData(false, false, true)] [InlineData(false, true, true)]
    [InlineData(true, false, true)] [InlineData(true, true, true)]
    public async Task NullablePrices_SurviveJsonSqliteAndOutboundWithoutInventingZero(bool hidePurchase, bool hideSales, bool deleted)
    {
        var dto = new ItemDto { Id=Guid.NewGuid(), NameOriginal="수량과 비고 유지", CurrentStock=7,
            SimpleMemo="보존 비고", PurchasePrice=hidePurchase ? null : 701m, SalePrice=hideSales ? null : 1101m,
            RetailPrice=hideSales ? null : 1201m, PriceGradeA=hideSales ? null : 1001m,
            PriceGradeB=hideSales ? null : 901m, PriceGradeC=hideSales ? null : 801m, IsDeleted=deleted };
        var json = JsonSerializer.Serialize(dto);
        var local = LocalMappings.ToLocal(JsonSerializer.Deserialize<ItemDto>(json)!);
        await using var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync(); db.Items.Add(local); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var saved = await db.Items.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(hidePurchase,saved.PurchaseAmountsHidden); Assert.Equal(hideSales,saved.SalesAmountsHidden);
        Assert.Equal(hidePurchase ? 0m : 701m,saved.PurchasePrice);
        Assert.Equal(hideSales ? 0m : 1101m,saved.SalePrice);
        saved.SimpleMemo="비금액 수정";
        var outbound=LocalMappings.ToDto(saved);
        Assert.Equal(dto.PurchasePrice,outbound.PurchasePrice); Assert.Equal(dto.SalePrice,outbound.SalePrice);
        Assert.Equal(dto.PriceGradeC,outbound.PriceGradeC); Assert.Equal(7m,outbound.CurrentStock);
        Assert.Equal("비금액 수정",outbound.SimpleMemo); Assert.Equal(deleted,outbound.IsDeleted);
        // A later authorized snapshot of real zero clears the hidden state.
        var disclosed=LocalMappings.ToLocal(new ItemDto { Id=dto.Id });
        db.Entry(saved).CurrentValues.SetValues(disclosed); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        saved=await db.Items.SingleAsync(); Assert.False(saved.SalesAmountsHidden); Assert.False(saved.PurchaseAmountsHidden);
        Assert.Equal(0m,LocalMappings.ToDto(saved).SalePrice);
    }

    [Theory]
    [InlineData("PurchasePrice")] [InlineData("SalePrice")] [InlineData("RetailPrice")]
    [InlineData("PriceGradeA")] [InlineData("PriceGradeB")] [InlineData("PriceGradeC")]
    public void ExplicitNull_IsUnknown_OmittedLegacyPriceRemainsKnownZero(string field)
    {
        var hidden=JsonSerializer.Deserialize<ItemDto>("{\""+field+"\":null}")!;
        var property=typeof(ItemDto).GetProperty(field)!;
        Assert.Null(property.GetValue(hidden));
        Assert.Equal(0m,property.GetValue(JsonSerializer.Deserialize<ItemDto>("{}")!));
        var mapped=LocalMappings.ToDto(LocalMappings.ToLocal(hidden));
        Assert.Null(property.GetValue(mapped));
        if (field != "PurchasePrice") Assert.True(mapped.SalesAmountsHidden);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task GradePrice_HiddenAndKnownZeroRemainDifferent(bool hidden)
    {
        var dto=JsonSerializer.Deserialize<ItemPriceGradeDto>(hidden ? "{\"UnitPrice\":null}" : "{\"UnitPrice\":0}")!;
        dto.Id=Guid.NewGuid(); var local=LocalMappings.ToLocal(dto);
        Assert.Equal(hidden,local.AmountsHidden);
        Assert.Equal(dto.UnitPrice,LocalMappings.ToDto(local).UnitPrice);
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        // This tests the cache column independently of parent item/option setup.
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
        db.ItemPriceGrades.Add(local);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        Assert.Equal(hidden,(await db.ItemPriceGrades.SingleAsync()).AmountsHidden);
    }

    [Fact]
    public async Task ExistingInitializedDatabase_AddsHiddenColumnsWithoutChangingPrices()
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.Items.Add(new LocalItem { Id=Guid.NewGuid(),NameOriginal="기존 품목",NameMatchKey="기존품목",PurchasePrice=701m,SalePrice=1101m });
        await db.SaveChangesAsync();db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Items DROP COLUMN PurchaseAmountsHidden;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Items DROP COLUMN SalesAmountsHidden;");
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE ItemPriceGrades DROP COLUMN AmountsHidden;");
        var method=typeof(LocalDbInitializer).GetMethod("ApplySchemaMaintenanceAsync",BindingFlags.Static|BindingFlags.NonPublic)!;
        await (Task)method.Invoke(null,[db])!;
        await (Task)method.Invoke(null,[db])!;
        var item=await db.Items.SingleAsync();Assert.Equal(701m,item.PurchasePrice);Assert.Equal(1101m,item.SalePrice);
        Assert.False(item.PurchaseAmountsHidden);Assert.False(item.SalesAmountsHidden);
        Assert.Empty(await db.ItemPriceGrades.ToListAsync());
    }

    [Theory]
    [InlineData(false,false)] [InlineData(false,true)] [InlineData(true,false)] [InlineData(true,true)]
    public void MobileUnknownSource_IsNotOverwrittenByEditorZeros(bool sales,bool purchase)
    {
        var source=new ItemDto { PurchasePrice=null,SalePrice=null,RetailPrice=null,PriceGradeA=null,PriceGradeB=null,PriceGradeC=null };
        var target=new ItemDto { SimpleMemo="보존",CurrentStock=9 };
        var access=new MobileItemAmountAccess(purchase,sales,true);
        access.ApplyEditedPrices(target,source,0,0,0);
        Assert.Null(target.PurchasePrice);Assert.Null(target.SalePrice);Assert.Null(target.PriceGradeC);
        Assert.Equal("보존",target.SimpleMemo);Assert.Equal(9m,target.CurrentStock);
        Assert.DoesNotContain("0원",access.Summary(source));Assert.Contains("비공개",access.ListSummary(source));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MobileDraft_UsesTheDocumentDirection(bool purchase)
    {
        var item=new ItemDto { PurchasePrice=purchase ? 701m : null,SalePrice=purchase ? null : 1101m };
        var line=InvoiceLineDraftItem.FromItem(item,2,purchase:purchase);
        Assert.False(line.AmountsHidden);Assert.Equal(purchase ? 701m : 1101m,line.UnitPrice);
        var hidden=InvoiceLineDraftItem.FromItem(item,2,purchase:!purchase);
        Assert.True(hidden.AmountsHidden);Assert.Null(hidden.ToDto(Guid.NewGuid()).UnitPrice);
        Assert.Equal(2m,hidden.Quantity);
    }
}
