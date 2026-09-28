using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InvoiceScopeAndQuantityIntegrityTests
{
    [Theory]
    [InlineData(false,false)]
    [InlineData(false,true)]
    [InlineData(true,false)]
    [InlineData(true,true)]
    public async Task HiddenPayment_CreateAndUpdateRejectUnknownMoneyWithoutBusinessMutation(bool sync, bool update)
    {
        var user=CreateAdminUser();
        await using var db=CreateDbContext(user);
        var customer=CreateCustomer(OfficeCodeCatalog.Usenet);
        var item=CreateItem(ItemTrackingTypes.NonStock,0);
        var invoice=CreateInvoice(Guid.NewGuid(),customer,item,10,ItemTrackingTypes.NonStock);
        invoice.TotalAmount=1000;
        db.AddRange(customer,item,invoice);
        var paymentId=Guid.NewGuid();
        if(update) db.Payments.Add(new Payment {Id=paymentId,InvoiceId=invoice.Id,Amount=123,Note="PRESERVE"});
        await db.SaveChangesAsync();
        var before=update ? await db.Payments.AsNoTracking().SingleAsync() : null;
        var dto=new PaymentDto {Id=paymentId,InvoiceId=invoice.Id,Amount=null,Note="UNKNOWN",PaymentDate=new DateOnly(2026,9,24),
            Revision=before?.Revision ?? 0,ExpectedRevision=before?.Revision ?? 0,MutationId=Guid.NewGuid().ToString("N"),MutationCreatedAtUtc=DateTime.UtcNow};
        if(sync)
        {
            var response=await CreateSyncController(db,user).Push(new SyncPushRequest {DeviceId="hidden-payment",Payments=[dto]},CancellationToken.None);
            var result=Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
            Assert.Equal(0,result.AcceptedCount);
            Assert.Single(result.Conflicts);
        }
        else
        {
            var controller=new PaymentsController(db,new OfficeScopeService(user,db),new StubCentralFileStorage(),new RentalSettlementRecalculationService(db))
                {ControllerContext=new ControllerContext {HttpContext=new DefaultHttpContext()}};
            var response=update ? await controller.Update(paymentId,dto,CancellationToken.None) : await controller.Create(dto,CancellationToken.None);
            Assert.IsType<BadRequestObjectResult>(response.Result);
        }
        db.ChangeTracker.Clear();
        if(update)
        {
            var stored=await db.Payments.SingleAsync();
            Assert.Equal(123m,stored.Amount);Assert.Equal("PRESERVE",stored.Note);Assert.Equal(before!.Revision,stored.Revision);
        }
        else Assert.Empty(await db.Payments.ToListAsync());
        Assert.Empty(await db.Transactions.ToListAsync());
        Assert.Empty(await db.ProcessedSyncMutations.ToListAsync());
    }

    [Fact]
    public void HiddenPayment_StorageMappingRejectsUnknownButTombstoneKeepsHistoricalAmount()
    {
        var payment=new Payment {Amount=123};
        Assert.Throws<InvalidOperationException>(()=>payment.Apply(new PaymentDto {Amount=null}));
        payment.Apply(new PaymentDto {Amount=null,IsDeleted=true});
        Assert.Equal(123m,payment.Amount);Assert.True(payment.IsDeleted);
    }
}
