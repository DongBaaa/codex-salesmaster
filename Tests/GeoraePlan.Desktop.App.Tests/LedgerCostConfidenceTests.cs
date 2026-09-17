using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;
public sealed class CostConfidenceTests
{
    [Theory]
    [InlineData("USENET",4)] [InlineData("USENET",2)] [InlineData("USENET",0)]
    [InlineData("YEONSU",4)] [InlineData("YEONSU",2)] [InlineData("YEONSU",0)]
    [InlineData("ITWORLD",4)] [InlineData("ITWORLD",2)] [InlineData("ITWORLD",0)]
    [InlineData("USENET",4,"zero-cost")]
    [InlineData("USENET",4,"missing")]
    [InlineData("USENET",4,"partial")]
    [InlineData("USENET",4,"over")]
    [InlineData("USENET",4,"pending")]
    [InlineData("USENET",4,"other-line-unsettled")]
    [InlineData("USENET",4,"negative-sale")]
    public async Task UnsettledCostsMustBeDisclosedInLedgerSummary(string office,int received,string scenario="normal")
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tenant=office=="ITWORLD"?"ITWORLD":"USENET_GROUP";var owner=office=="YEONSU"?"USENET":office;
        var session=new SessionState();session.SetOfflineSession(new UserSessionDto {Username="audit",Role=DomainConstants.RoleAdmin,TenantCode=tenant,OfficeCode=office,ScopeType=TenantScopeCatalog.ScopeAdmin});
        var local=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),session);
        var customer=Guid.NewGuid();var item=Guid.NewGuid();var purchase=Guid.NewGuid();var sale=Guid.NewGuid();
        var today=DateOnly.FromDateTime(DateTime.Today);var stamp=DateTime.UtcNow.AddDays(-2);
        db.Customers.Add(new LocalCustomer {Id=customer,TenantCode=tenant,OfficeCode=owner,ResponsibleOfficeCode=office,NameOriginal="isolated confidence customer",IsDirty=false});
        db.Items.Add(new LocalItem {Id=item,TenantCode=tenant,OfficeCode=owner,NameOriginal="inventory confidence item",PurchasePrice=10,IsDirty=false});
        if(received>0)db.Invoices.Add(new LocalInvoice {Id=purchase,CustomerId=customer,TenantCode=tenant,OfficeCode=owner,ResponsibleOfficeCode=office,SourceWarehouseCode=office+"_MAIN",VersionGroupId=purchase,VersionNumber=1,IsLatestVersion=true,IsConfirmed=true,VoucherType=VoucherType.Purchase,InvoiceDate=today.AddDays(-1),CreatedAtUtc=stamp,LastSavedAtUtc=stamp,PurchaseReceivingRequired=true,PurchaseReceivingStatus=InvoiceReceivingStatuses.Confirmed,IsDirty=false,Lines=[new LocalInvoiceLine {Id=Guid.NewGuid(),InvoiceId=purchase,ItemId=item,ItemNameOriginal="inventory confidence item",Quantity=received,UnitPrice=10,LineAmount=received*10}]});
        db.Invoices.Add(new LocalInvoice {Id=sale,CustomerId=customer,TenantCode=tenant,OfficeCode=owner,ResponsibleOfficeCode=office,SourceWarehouseCode=office+"_MAIN",VersionGroupId=sale,VersionNumber=1,IsLatestVersion=true,IsConfirmed=true,VoucherType=VoucherType.Sales,InvoiceDate=today,CreatedAtUtc=stamp.AddDays(1),LastSavedAtUtc=stamp.AddDays(1),IsDirty=false,Lines=[new LocalInvoiceLine {Id=Guid.NewGuid(),InvoiceId=sale,ItemId=item,ItemNameOriginal="inventory confidence item",Quantity=4,UnitPrice=30,LineAmount=120}]});
        await db.SaveChangesAsync();
        await using(var tx=await db.Database.BeginTransactionAsync())
        {await (Task)typeof(LocalStateService).GetMethod("RefreshInventoryCostAfterPullAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(local,[false,CancellationToken.None])!;await tx.CommitAsync();}
        db.ChangeTracker.Clear();
        var status=(await db.Invoices.SingleAsync(x=>x.Id==sale)).CostStatus;
        var allocations=await db.CostAllocations.Where(x=>x.SalesInvoiceId==sale).ToListAsync();
        Assert.Equal(received==4?"Settled":"Unsettled",status);
        Assert.Equal(4,allocations.Sum(x=>x.Quantity));
        Assert.Equal(4-received,allocations.Where(x=>x.IsUnsettled).Sum(x=>x.Quantity));
        var expectedCost=received*10m;
        var expectedUncertain=received<4;
        if(scenario!="normal")
        {
            var saleEntity=await db.Invoices.Include(x=>x.Lines).SingleAsync(x=>x.Id==sale);
            var allocation=Assert.Single(allocations);
            switch(scenario)
            {
                case "zero-cost": allocation.UnitCost=0; allocation.CostAmount=0; expectedCost=0; break;
                case "missing": db.CostAllocations.Remove(allocation); expectedUncertain=true; break;
                case "partial": allocation.Quantity=2; allocation.CostAmount=20; expectedCost=20; expectedUncertain=true; break;
                case "over": allocation.Quantity=5; allocation.CostAmount=50; expectedCost=50; expectedUncertain=true; break;
                case "pending": saleEntity.CostStatus="Pending"; expectedUncertain=true; break;
                case "other-line-unsettled": saleEntity.CostStatus="Unsettled"; break;
                case "negative-sale": saleEntity.Lines.Single().Quantity=-4; break;
            }
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
        }
        var beforeRead=await db.Invoices.AsNoTracking().Select(x=>new {x.Id,x.IsDirty,x.Revision,x.CostStatus}).ToListAsync();
        await using var vm=new YeonsuDeliveryViewModel(local,session);await vm.InitializeAsync();vm.SelectedViewTarget=YeonsuDeliveryViewModel.ViewTargetSales;
        var row=Assert.Single(vm.Deliveries);
        Assert.Equal(expectedCost,row.PurchaseAmount);Assert.Equal(120-expectedCost,row.ProfitAmount);
        var disclosed=Regex.IsMatch(vm.StatusMessage,"미확정|미정산|잠정|추정");
        var result=new {office,received,sold=4,status,unsettledQuantity=allocations.Where(x=>x.IsUnsettled).Sum(x=>x.Quantity),row.PurchaseAmount,row.SalesAmount,row.ProfitAmount,row.FeeAmount,vm.SummaryProfitAmount,vm.SummaryFeeAmount,vm.StatusMessage,uncertaintyDisclosed=disclosed};
        if(Environment.GetEnvironmentVariable("AUDIT_CASE") is string evidence)
            await File.WriteAllTextAsync(Path.Combine(evidence,office+"-received-"+received+"-"+scenario+".json"),JsonSerializer.Serialize(result));
        Assert.Equal(expectedUncertain,disclosed);
        Assert.Equal(expectedUncertain,row.IsCostUncertain);
        Assert.Equal(expectedUncertain,row.ProfitAmountDisplay.Contains("잠정"));
        Assert.Equal(expectedUncertain,row.FeeAmountDisplay.Contains("잠정"));
        Assert.Equal(expectedUncertain,row.PurchaseAmountDisplay.Contains("잠정"));
        Assert.Equal(expectedUncertain,vm.SummaryProfitLabel.Contains("잠정"));
        Assert.Equal(expectedUncertain,vm.SummaryFeeLabel.Contains("잠정"));
        Assert.Equal(expectedUncertain,vm.SummaryPurchaseLabel.Contains("잠정"));
        var changes=new List<string?>(); vm.PropertyChanged+=(_,e)=>changes.Add(e.PropertyName);
        vm.SelectedViewTarget=YeonsuDeliveryViewModel.ViewTargetPurchase;
        Assert.Equal(0,vm.UncertainCostRowCount);
        Assert.DoesNotContain("잠정",vm.StatusMessage); Assert.DoesNotContain("잠정",vm.SummaryProfitLabel);
        Assert.All(vm.Deliveries,x=>Assert.False(x.IsCostUncertain));
        vm.SelectedViewTarget=YeonsuDeliveryViewModel.ViewTargetSales;
        Assert.Equal(expectedUncertain,Assert.Single(vm.Deliveries).IsCostUncertain);
        if(expectedUncertain) Assert.Contains(nameof(vm.SummaryProfitLabel),changes);
        vm.CustomerSearchText="no-match-unique";
        Assert.Empty(vm.Deliveries); Assert.Equal(0,vm.UncertainCostRowCount); Assert.DoesNotContain("잠정",vm.StatusMessage);
        vm.CustomerSearchText="";
        Assert.Equal(expectedUncertain,Assert.Single(vm.Deliveries).IsCostUncertain);
        await vm.LoadDeliveriesCommand.ExecuteAsync(null);
        Assert.Equal(expectedUncertain,Assert.Single(vm.Deliveries).IsCostUncertain);
        var afterRead=await db.Invoices.AsNoTracking().Select(x=>new {x.Id,x.IsDirty,x.Revision,x.CostStatus}).ToListAsync();
        Assert.Equal(beforeRead,afterRead);
    }
}
