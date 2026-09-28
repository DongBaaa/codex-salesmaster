using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Middleware;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InvoiceScopeAndQuantityIntegrityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AmountRead_PaymentValidationDoesNotRevealRestrictedOutstanding(bool sync, bool mayView)
    {
        var user=new TestCurrentUserContext { Username="payment-amount-validation", Permissions=mayView
            ? [PermissionNames.PaymentEdit,PermissionNames.AmountViewSales] : [PermissionNames.PaymentEdit] };
        await using var db=CreateDbContext(user);
        var customer=CreateCustomer(OfficeCodeCatalog.Usenet);var item=CreateItem(ItemTrackingTypes.NonStock,0);
        var invoice=CreateInvoice(Guid.NewGuid(),customer,item,3,ItemTrackingTypes.NonStock);invoice.TotalAmount=330;
        db.AddRange(customer,item,invoice);await db.SaveChangesAsync();
        var dto=new PaymentDto {Id=Guid.NewGuid(),InvoiceId=invoice.Id,Amount=999,PaymentDate=new DateOnly(2026,9,24)};
        if(sync)
        {
            var response=Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>((await CreateSyncController(db,user).Push(
                new SyncPushRequest { DeviceId="payment-error-read",Payments=[dto]},CancellationToken.None)).Result).Value);
            var conflict=Assert.Single(response.Conflicts);
            Assert.Equal(mayView,conflict.Reason.Contains("outstanding=330",StringComparison.Ordinal));
            Assert.Contains("exceeds current outstanding",conflict.Reason);
        }
        else
        {
            var controller=new PaymentsController(db,new OfficeScopeService(user,db),new StubCentralFileStorage(),new RentalSettlementRecalculationService(db));
            var result=Assert.IsType<ConflictObjectResult>((await controller.Create(dto,CancellationToken.None)).Result);
            using var json=JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
            Assert.Equal(mayView ? JsonValueKind.Number : JsonValueKind.Null,json.RootElement.GetProperty("outstandingAmount").ValueKind);
            if(mayView) Assert.Equal(330m,json.RootElement.GetProperty("outstandingAmount").GetDecimal());
        }
        Assert.Empty(await db.Payments.ToListAsync());Assert.Empty(await db.Transactions.ToListAsync());
    }

    public static IEnumerable<object[]> AmountReadCases()
    {
        foreach (var type in Enum.GetValues<VoucherType>())
        foreach (var permission in new[] { "none", "matching", "opposite", "admin" })
            yield return [type, permission];
    }

    [Theory, MemberData(nameof(AmountReadCases))]
    public async Task AmountRead_ListDetailPaymentsAndPullUseMatchingPermissionWithoutChangingStorage(VoucherType type, string permission)
    {
        var sales = type is VoucherType.Sales or VoucherType.Collection;
        var grantSales = permission == "matching" ? sales : !sales;
        var user = new TestCurrentUserContext { Username="amount-reader", IsAdmin=permission=="admin",
            Permissions=permission=="none" ? [] : [grantSales ? PermissionNames.AmountViewSales : PermissionNames.AmountViewPurchase] };
        await using var db = CreateDbContext(user);
        var customer=CreateCustomer(OfficeCodeCatalog.Usenet);
        var item=CreateItem(ItemTrackingTypes.NonStock,0);
        var entity=CreateInvoice(Guid.NewGuid(),customer,item,3,ItemTrackingTypes.NonStock);
        entity.VoucherType=type; entity.TotalAmount=330;entity.SupplyAmount=300;entity.VatAmount=30;
        entity.Memo="KEEP-MEMO";entity.Lines.Single().Remark="KEEP-REMARK";
        var payment=new Payment { Id=Guid.NewGuid(),InvoiceId=entity.Id,Amount=123,Note="KEEP-NOTE" };
        db.AddRange(customer,item,entity,payment);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var revision=entity.Revision;
        var mayView=permission is "matching" or "admin";
        var controller=CreateInvoicesController(db,user);
        var list=Assert.IsType<List<InvoiceDto>>(Assert.IsType<OkObjectResult>((await controller.GetAll(null,null,cancellationToken:CancellationToken.None)).Result).Value);
        var detail=Assert.IsType<InvoiceDto>(Assert.IsType<OkObjectResult>((await controller.GetById(entity.Id,CancellationToken.None)).Result).Value);
        var paymentsController=new PaymentsController(db,new OfficeScopeService(user,db),new StubCentralFileStorage(),new RentalSettlementRecalculationService(db));
        var payments=Assert.IsType<List<PaymentDto>>(Assert.IsType<OkObjectResult>((await paymentsController.GetByInvoice(entity.Id,CancellationToken.None)).Result).Value);
        var pull=Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>((await CreateSyncController(db,user).Pull(0,CancellationToken.None)).Result).Value);
        foreach(var dto in new[] { Assert.Single(list),detail,Assert.Single(pull.Invoices) })
        {
            Assert.Equal(mayView ? 330m : null,dto.TotalAmount);
            Assert.Equal(mayView ? 300m : null,dto.SupplyAmount);
            Assert.Equal(mayView ? 30m : null,dto.VatAmount);
            var line=Assert.Single(dto.Lines);
            Assert.Equal(mayView ? 100m : null,line.UnitPrice);
            Assert.Equal(mayView ? 300m : null,line.LineAmount);
            Assert.Equal(3m,line.Quantity);Assert.Equal("KEEP-REMARK",line.Remark);Assert.Equal("KEEP-MEMO",dto.Memo);
            Assert.Equal(mayView ? 123m : null,Assert.Single(dto.Payments).Amount);
        }
        Assert.Equal(mayView ? 123m : null,Assert.Single(payments).Amount);
        Assert.Equal(mayView ? 123m : null,Assert.Single(pull.Payments).Amount);
        var stored=await db.Invoices.Include(x=>x.Lines).AsNoTracking().SingleAsync();
        Assert.Equal(330m,stored.TotalAmount);Assert.Equal(revision,stored.Revision);
        Assert.Equal(100m,stored.Lines.Single().UnitPrice);
        Assert.Equal(123m,(await db.Payments.AsNoTracking().SingleAsync()).Amount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmountRead_RestrictedCreateAndReplayHideServerCalculatedMoney(bool sync)
    {
        var user=AmountUser(VoucherType.Sales,false);
        await using var db=CreateDbContext(user);
        var customer=CreateCustomer(OfficeCodeCatalog.Usenet);var item=CreateItem(ItemTrackingTypes.NonStock,0);
        item.SalePrice=1100; db.AddRange(customer,item);await db.SaveChangesAsync();
        var dto=BuildInvoiceDto(Guid.NewGuid(),customer,item,OfficeCodeCatalog.UsenetMainWarehouse,3,ItemTrackingTypes.NonStock);
        dto.TotalAmount=dto.SupplyAmount=dto.VatAmount=null;dto.Lines[0].UnitPrice=dto.Lines[0].LineAmount=null;
        dto.MutationId=Guid.NewGuid().ToString("N");dto.MutationCreatedAtUtc=DateTime.UtcNow;
        var raw=JsonSerializer.Serialize(dto);
        for(var i=0;i<2;i++)
        {
            var request=JsonSerializer.Deserialize<InvoiceDto>(raw)!;
            if(sync) await SaveAmountInvoice(true,db,user,request,false,duplicate:i==1);
            else
            {
                var response=Assert.IsType<InvoiceDto>(Assert.IsType<OkObjectResult>((await CreateInvoicesController(db,user).Create(request,CancellationToken.None)).Result).Value);
                Assert.Null(response.TotalAmount);Assert.Null(response.Lines[0].UnitPrice);
            }
        }
        db.ChangeTracker.Clear();var stored=await db.Invoices.Include(x=>x.Lines).SingleAsync();
        Assert.Equal(3300m,stored.TotalAmount);Assert.Equal(1100m,stored.Lines.Single().UnitPrice);
        Assert.Single(await db.ProcessedSyncMutations.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AmountRead_ConflictReplyHidesMoneyButAuditRecordKeepsOriginal(bool matchingPermission)
    {
        var user=AmountUser(VoucherType.Sales,matchingPermission);
        await using var db=CreateDbContext(user);
        var customer=CreateCustomer(OfficeCodeCatalog.Usenet);var item=CreateItem(ItemTrackingTypes.NonStock,0);
        var invoice=CreateInvoice(Guid.NewGuid(),customer,item,3,ItemTrackingTypes.NonStock);
        invoice.TotalAmount=330;invoice.SupplyAmount=300;invoice.VatAmount=30;
        db.AddRange(customer,item,invoice);await db.SaveChangesAsync();
        var request=invoice.ToDto();request.ExpectedRevision=invoice.Revision+500;request.MutationId=Guid.NewGuid().ToString("N");
        var response=Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>((await CreateSyncController(db,user).Push(new SyncPushRequest {DeviceId="amount-read",Invoices=[request]},CancellationToken.None)).Result).Value);
        var conflict=Assert.Single(response.Conflicts);
        var wire=JsonSerializer.Deserialize<InvoiceDto>(conflict.ServerJson)!;
        Assert.Equal(matchingPermission ? 330m : null,wire.TotalAmount);
        Assert.Equal(matchingPermission ? 100m : null,wire.Lines[0].UnitPrice);
        Assert.Equal(3m,wire.Lines[0].Quantity);
        var stored=await db.ConflictLogs.AsNoTracking().SingleAsync();
        Assert.Equal(330m,JsonSerializer.Deserialize<InvoiceDto>(stored.ServerJson)!.TotalAmount);
    }

    [Theory]
    [InlineData("/invoices",0,false,false)]
    [InlineData("/invoices",1,false,false)]
    [InlineData("/payments",1,false,false)]
    [InlineData("/sync/pull",1,false,false)]
    [InlineData("/sync/push",1,false,false)]
    [InlineData("/sync/pull",3,false,false)]
    [InlineData("/sync/push",3,false,false)]
    [InlineData("/sync/pull",4,false,true)]
    [InlineData("/sync/push",4,false,true)]
    [InlineData("/customers/11111111-1111-1111-1111-111111111111/detail",1,false,false)]
    [InlineData("/invoices",2,false,true)]
    [InlineData("/invoices",1,true,true)]
    [InlineData("/updates/stable",1,false,true)]
    public async Task AmountRead_CompatibilityGateRequiresNullableSchemaBeforeEndpointRuns(string path,int protocol,bool admin,bool allowed)
    {
        var user=admin ? CreateAdminUser() : AmountUser(VoucherType.Sales,false);
        await using var db=CreateDbContext(user);
        var context=new DefaultHttpContext();context.Request.Path=path;
        context.User=new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name,"reader")],"test"));
        context.Response.Body=new MemoryStream();
        if(protocol!=0)
        {
            context.Request.Headers[ClientCompatibilityHeaders.AppId]="georaeplan-desktop";
            context.Request.Headers[ClientCompatibilityHeaders.Platform]="windows";
            context.Request.Headers[ClientCompatibilityHeaders.Version]="1.1.743";
            context.Request.Headers[ClientCompatibilityHeaders.Build]="743";
            context.Request.Headers[ClientCompatibilityHeaders.Protocol]=protocol.ToString();
        }
        var called=false;
        var middleware=new InvoiceAmountCompatibilityMiddleware(_=>{called=true;return Task.CompletedTask;});
        await middleware.InvokeAsync(context,new OfficeScopeService(user,db));
        Assert.Equal(allowed,called);
        if(!allowed)
        {
            Assert.Equal(426,context.Response.StatusCode);Assert.Equal("no-store",context.Response.Headers.CacheControl);
            context.Response.Body.Position=0;
            var response=await JsonSerializer.DeserializeAsync<ClientUpgradeRequiredResponse>(context.Response.Body,new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.Equal(path.StartsWith("/sync",StringComparison.Ordinal) ? 4 : 2,response!.Required.MinimumProtocolVersion);Assert.True(response.Required.RequiresUserAction);
        }
    }
}
