using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Middleware;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InvoiceScopeAndQuantityIntegrityTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task ItemRead_ListDetailPullAndGradePreserveStorage(int mask)
    {
        var user = ItemAmountUser(mask); await using var db = CreateDbContext(user);
        var item = PricedReadItem(); var option = new PriceGradeOption { Name = "read grade", PriceSource = "Sales" };
        var grade = new ItemPriceGrade { ItemId = item.Id, PriceGradeOptionId = option.Id, PriceGradeName = option.Name, UnitPrice = 1301, IsActive = true };
        db.AddRange(item, option, grade); await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        var revision = item.Revision;
        var controller = new ItemsController(db, new OfficeScopeService(user, db));
        var list = Assert.IsType<List<ItemDto>>(Assert.IsType<OkObjectResult>((await controller.GetAll(null, null, null, null, CancellationToken.None)).Result).Value);
        var single = Assert.IsType<ItemDto>(Assert.IsType<OkObjectResult>((await controller.GetById(item.Id, CancellationToken.None)).Result).Value);
        var detail = Assert.IsType<ItemDetailDto>(Assert.IsType<OkObjectResult>((await controller.GetDetail(item.Id, CancellationToken.None)).Result).Value);
        var pull = Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>((await CreateSyncController(db, user).Pull(0, CancellationToken.None)).Result).Value);
        foreach (var dto in new[] { Assert.Single(list), single, detail.Item, Assert.Single(pull.Items) }) AssertItemRead(dto, mask);
        Assert.Equal(mask == 4 || (mask & 1) != 0 ? 1301m : null, Assert.Single(pull.ItemPriceGrades).UnitPrice);
        Assert.Equal(701m, (await db.Items.AsNoTracking().SingleAsync()).PurchasePrice);
        Assert.Equal(1101m, (await db.Items.AsNoTracking().SingleAsync()).SalePrice);
        Assert.Equal(revision, (await db.Items.AsNoTracking().SingleAsync()).Revision);
        Assert.Equal(1301m, (await db.ItemPriceGrades.AsNoTracking().SingleAsync()).UnitPrice);
    }

    [Theory]
    [InlineData(false,0)] [InlineData(false,1)] [InlineData(false,2)] [InlineData(false,3)] [InlineData(false,4)]
    [InlineData(true,0)] [InlineData(true,1)] [InlineData(true,2)] [InlineData(true,3)] [InlineData(true,4)]
    public async Task ItemRead_DirectSaveAndExactReplayReturnScopedCopies(bool create, int mask)
    {
        var user = ItemAmountUser(mask); await using var db = CreateDbContext(user);
        var item = PricedReadItem(); if (!create) { db.Items.Add(item); await db.SaveChangesAsync(); }
        var dto = item.ToDto(); dto.ExpectedRevision = create ? 0 : item.Revision;
        dto.MutationId = Guid.NewGuid().ToString("N"); dto.MutationCreatedAtUtc = DateTime.UtcNow;
        var original = JsonSerializer.Serialize(dto); var controller = new ItemsController(db, new OfficeScopeService(user, db));
        for (var retry = 0; retry < 2; retry++)
        {
            var request = JsonSerializer.Deserialize<ItemDto>(original)!;
            var result = create ? await controller.Create(request, CancellationToken.None) : await controller.Update(dto.Id, request, CancellationToken.None);
            var response = Assert.IsType<ItemDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
            AssertItemRead(response, mask);
        }
        Assert.Single(await db.ProcessedSyncMutations.ToListAsync());
        var stored = await db.Items.AsNoTracking().SingleAsync();
        Assert.Equal(create && mask != 4 && (mask & 2) == 0 ? 0m : 701m, stored.PurchasePrice);
        Assert.Equal(create && mask != 4 && (mask & 1) == 0 ? 0m : 1101m, stored.SalePrice);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task ItemRead_SyncConflictHidesBothCopiesButPreservesAudit(int mask)
    {
        var user = ItemAmountUser(mask); await using var db = CreateDbContext(user);
        var item = PricedReadItem(); db.Items.Add(item); await db.SaveChangesAsync();
        var dto = item.ToDto(); dto.ExpectedRevision = item.Revision + 100; dto.MutationId = Guid.NewGuid().ToString("N");
        var response = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>((await CreateSyncController(db,user).Push(new SyncPushRequest { DeviceId="item-read-conflict", Items=[dto] },CancellationToken.None)).Result).Value);
        var conflict = Assert.Single(response.Conflicts);
        AssertItemRead(JsonSerializer.Deserialize<ItemDto>(conflict.ServerJson)!,mask);
        AssertItemRead(JsonSerializer.Deserialize<ItemDto>(conflict.ClientJson)!,mask);
        var stored = await db.ConflictLogs.AsNoTracking().SingleAsync();
        Assert.Equal(701m,JsonSerializer.Deserialize<ItemDto>(stored.ServerJson)!.PurchasePrice);
        Assert.Equal(1101m,JsonSerializer.Deserialize<ItemDto>(stored.ClientJson)!.SalePrice);
    }

    [Theory]
    [InlineData("/items", 2, false)] [InlineData("/items", 3, true)]
    [InlineData("/sync/pull", 2, false)] [InlineData("/sync/pull", 3, false)] [InlineData("/sync/pull", 4, true)]
    [InlineData("/sync/push", 2, false)] [InlineData("/sync/push", 3, false)] [InlineData("/sync/push", 4, true)]
    [InlineData("/invoices", 2, true)] [InlineData("/items-extra", 2, true)]
    public async Task ItemRead_CompatibilityRequiresCatalogSchemaWithoutBlockingInvoiceV2(string path,int version,bool allowed)
    {
        var user=ItemAmountUser(0);await using var db=CreateDbContext(user);
        var context=new DefaultHttpContext();context.Request.Path=path;
        context.User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name,"reader")],"test"));context.Response.Body=new MemoryStream();
        context.Request.Headers[ClientCompatibilityHeaders.AppId]="georaeplan-desktop";
        context.Request.Headers[ClientCompatibilityHeaders.Platform]="windows";
        context.Request.Headers[ClientCompatibilityHeaders.Version]="1.1.743";
        context.Request.Headers[ClientCompatibilityHeaders.Build]="743";
        context.Request.Headers[ClientCompatibilityHeaders.Protocol]=version.ToString();
        var called=false;await new InvoiceAmountCompatibilityMiddleware(_=>{called=true;return Task.CompletedTask;}).InvokeAsync(context,new OfficeScopeService(user,db));
        Assert.Equal(allowed,called);
        if(!allowed)
        {
            Assert.Equal(426,context.Response.StatusCode);Assert.Equal("no-store",context.Response.Headers.CacheControl);
            context.Response.Body.Position=0;
            var result=await JsonSerializer.DeserializeAsync<ClientUpgradeRequiredResponse>(context.Response.Body,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(path.StartsWith("/sync/",StringComparison.Ordinal) ? 4 : 3,result!.Required.MinimumProtocolVersion);
        }
    }

    private static Item PricedReadItem()
    {
        var item=CreateItem(ItemTrackingTypes.NonStock,0);
        item.PurchasePrice=701;item.SalePrice=1101;item.RetailPrice=1201;item.PriceGradeA=1001;item.PriceGradeB=901;item.PriceGradeC=801;
        item.SimpleMemo="keep memo";return item;
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task ItemRead_OmittedLegacyConflictPricesBecomeExplicitNullWhenRestricted(int mask)
    {
        var user=ItemAmountUser(mask);await using var db=CreateDbContext(user);var scope=new OfficeScopeService(user,db);
        var item=new ConflictLogDto {EntityName="Item",ServerJson="{}",ClientJson="{}"};
        var grade=new ConflictLogDto {EntityName="ItemPriceGrade",ServerJson="{}",ClientJson="{}"};
        ItemAmountReadPolicy.ApplyConflicts([item,grade],scope);
        foreach(var raw in new[]{item.ServerJson,item.ClientJson})
        {
            var dto=JsonSerializer.Deserialize<ItemDto>(raw)!;
            Assert.Equal((mask&2)!=0?0m:null,dto.PurchasePrice);
            Assert.Equal((mask&1)!=0?0m:null,dto.SalePrice);
            Assert.Equal((mask&1)!=0?0m:null,dto.PriceGradeC);
        }
        Assert.Equal((mask&1)!=0?0m:null,JsonSerializer.Deserialize<ItemPriceGradeDto>(grade.ServerJson)!.UnitPrice);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task ItemRead_ConflictJsonHandlesGradeCaseNestedFieldsAndMalformedSnapshots(int mask)
    {
        var user=ItemAmountUser(mask);await using var db=CreateDbContext(user);var scope=new OfficeScopeService(user,db);
        var itemJson="{\"purchasePrice\":701,\"salePrice\":1101,\"salesAmountsHidden\":false,\"extra\":{\"PURCHASEPRICE\":701,\"PriceGradeA\":1001},\"simpleMemo\":\"keep\"}";
        var gradeJson="{\"unitPrice\":1301,\"amountsHidden\":false,\"extra\":[{\"UNITPRICE\":1301}],\"priceGradeName\":\"keep\"}";
        var item=new ConflictLogDto {EntityName="Item",ServerJson=itemJson,ClientJson="{malformed"};
        var grade=new ConflictLogDto {EntityName="ItemPriceGrade",ServerJson=gradeJson,ClientJson="[]"};
        ItemAmountReadPolicy.ApplyConflicts([item,grade],scope);
        var purchase=mask==4||(mask&2)!=0;var sales=mask==4||(mask&1)!=0;
        using var i=JsonDocument.Parse(item.ServerJson);using var g=JsonDocument.Parse(grade.ServerJson);
        Assert.Equal(purchase?JsonValueKind.Number:JsonValueKind.Null,i.RootElement.GetProperty("purchasePrice").ValueKind);
        Assert.Equal(sales?JsonValueKind.Number:JsonValueKind.Null,i.RootElement.GetProperty("salePrice").ValueKind);
        Assert.Equal(purchase?JsonValueKind.Number:JsonValueKind.Null,i.RootElement.GetProperty("extra").GetProperty("PURCHASEPRICE").ValueKind);
        Assert.Equal(sales?JsonValueKind.Number:JsonValueKind.Null,i.RootElement.GetProperty("extra").GetProperty("PriceGradeA").ValueKind);
        Assert.Equal(sales?JsonValueKind.Number:JsonValueKind.Null,g.RootElement.GetProperty("unitPrice").ValueKind);
        Assert.Equal(sales?JsonValueKind.Number:JsonValueKind.Null,g.RootElement.GetProperty("extra")[0].GetProperty("UNITPRICE").ValueKind);
        Assert.Equal(!sales,i.RootElement.GetProperty("salesAmountsHidden").GetBoolean());
        Assert.Equal(!sales,g.RootElement.GetProperty("amountsHidden").GetBoolean());
        Assert.Equal("keep",i.RootElement.GetProperty("simpleMemo").GetString());
        Assert.Equal(purchase&&sales?"{malformed":string.Empty,item.ClientJson);
        Assert.Equal(sales?"[]":string.Empty,grade.ClientJson);
    }
    private static void AssertItemRead(ItemDto dto,int mask)
    {
        var purchase=mask==4||(mask&2)!=0;var sales=mask==4||(mask&1)!=0;
        Assert.Equal(purchase?701m:null,dto.PurchasePrice);Assert.Equal(sales?1101m:null,dto.SalePrice);
        Assert.Equal(sales?1201m:null,dto.RetailPrice);Assert.Equal(sales?1001m:null,dto.PriceGradeA);
        Assert.Equal(sales?901m:null,dto.PriceGradeB);Assert.Equal(sales?801m:null,dto.PriceGradeC);
        Assert.Equal("keep memo",dto.SimpleMemo);Assert.Equal(0m,dto.CurrentStock);
    }
}
