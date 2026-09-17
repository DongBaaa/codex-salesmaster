using System.Net.Http;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncRetryParentScopeTests
{
    [Theory]
    [InlineData("payment", "USENET")]
    [InlineData("payment", "YEONSU")]
    [InlineData("payment", "ITWORLD")]
    [InlineData("contract", "USENET")]
    [InlineData("contract", "YEONSU")]
    [InlineData("contract", "ITWORLD")]
    [InlineData("price", "USENET")]
    [InlineData("price", "YEONSU")]
    [InlineData("price", "ITWORLD")]
    [InlineData("attachment", "USENET")]
    [InlineData("attachment", "YEONSU")]
    [InlineData("attachment", "ITWORLD")]
    public async Task RequeueRetainsAuthoritativeParentScopeAndOriginalSessionOwner(string kind, string office)
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);await db.Database.EnsureCreatedAsync();
        var tenant=office=="ITWORLD"?"ITWORLD":"USENET_GROUP";var owner=office=="YEONSU"?"USENET":office;
        var parent=Guid.NewGuid();var id=Guid.NewGuid();
        SyncEntityDto dto;
        string entityName;
        switch(kind)
        {
            case "payment":
                db.Invoices.Add(new LocalInvoice {Id=parent,TenantCode=tenant,OfficeCode=owner,ResponsibleOfficeCode=office,IsDirty=false});
                dto=new PaymentDto {Id=id,InvoiceId=parent};entityName=nameof(LocalPayment);break;
            case "contract":
                db.Customers.Add(new LocalCustomer {Id=parent,TenantCode=tenant,OfficeCode=owner,ResponsibleOfficeCode=office,IsDirty=false});
                dto=new CustomerContractDto {Id=id,CustomerId=parent};entityName=nameof(LocalCustomerContract);break;
            case "price":
                owner=OfficeCodeCatalog.Shared;
                db.Items.Add(new LocalItem {Id=parent,TenantCode=tenant,OfficeCode=owner,IsDirty=false});
                dto=new ItemPriceGradeDto {Id=id,ItemId=parent};entityName=nameof(LocalItemPriceGrade);break;
            default:
                db.Transactions.Add(new LocalTransaction {Id=parent,TenantCode=tenant,OfficeCode=owner,ResponsibleOfficeCode=office,IsDirty=false});
                dto=new TransactionAttachmentDto {Id=id,TransactionId=parent};entityName=nameof(LocalTransactionAttachment);break;
        }
        var session=new SessionState();session.SetSession("synthetic-token",new UserSessionDto {UserId=Guid.NewGuid(),Username="scope-test",Role="User",TenantCode=tenant,OfficeCode=office,ScopeType=TenantScopeCatalog.ScopeOfficeOnly},DateTime.UtcNow.AddHours(1));
        var original=new LocalSyncOutboxEntry {Id=Guid.NewGuid(),MutationId="previous",DeviceId="device",EntityName=entityName,EntityId=id,ExpectedRevision=10,TenantCode=tenant,OfficeCode=owner,ResponsibleOfficeCode=office,BusinessDatabaseName=session.SelectedBusinessDatabaseName,UserId=session.User!.UserId,SessionId=session.SessionId,Status="Failed",PreparedAtUtc=DateTime.UtcNow};
        db.SyncOutboxEntries.Add(original);await db.SaveChangesAsync();
        var dispatcher=new SyncRequestDispatcher();var local=new LocalStateService(db,new OfficeAccessService(),dispatcher,session);
        using var http=new HttpClient {BaseAddress=new Uri("http://127.0.0.1/")};
        using var sync=new SyncService(db,local,new RentalStateService(db),new ErpApiClient(http,session),session,dispatcher,new SyncDiagnosticsService(session));
        dto.Revision=20;dto.UpdatedAtUtc=DateTime.UtcNow;
        var method=typeof(SyncService).GetMethod("RequeuePreparedMutationAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.MakeGenericMethod(dto.GetType());
        await (Task)method.Invoke(sync,[entityName,id,"previous",dto,"device",session,CancellationToken.None])!;await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var row=await db.SyncOutboxEntries.SingleAsync();
        Assert.Equal(original.Id,row.Id);Assert.Equal(20,row.ExpectedRevision);Assert.Equal("Prepared",row.Status);Assert.NotEqual("previous",row.MutationId);
        Assert.Equal(tenant,row.TenantCode);Assert.Equal(owner,row.OfficeCode);Assert.Equal(office,row.ResponsibleOfficeCode);
        Assert.Equal(original.BusinessDatabaseName,row.BusinessDatabaseName);Assert.Equal(original.UserId,row.UserId);Assert.Equal(original.SessionId,row.SessionId);Assert.Equal("device",row.DeviceId);
    }
}
