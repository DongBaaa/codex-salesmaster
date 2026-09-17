using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SalesEditSessionReadinessTests
{
    [Theory]
    [InlineData("USENET", "Sales")]
    [InlineData("USENET", "Purchase")]
    [InlineData("YEONSU", "Sales")]
    [InlineData("YEONSU", "Purchase")]
    [InlineData("ITWORLD", "Sales")]
    [InlineData("ITWORLD", "Purchase")]
    public async Task OnlyLoadedInvoiceIsEligible_NewDraftClearsPreviousSubject(string office, string voucherName)
    {
        var voucher = Enum.Parse<VoucherType>(voucherName);
        var property = typeof(SalesViewModel).GetProperty("EditSessionInvoiceId");
        Assert.NotNull(property);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var tenant = office == "ITWORLD" ? "ITWORLD" : "USENET_GROUP";
        var owner = office == "YEONSU" ? "USENET" : office;
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { Username="readiness-test", Role="User", TenantCode=tenant, OfficeCode=office, ScopeType="OfficeOnly" });
        var customer = new LocalCustomer { Id=Guid.NewGuid(), NameOriginal="Synthetic readiness", TenantCode=tenant, OfficeCode=owner, ResponsibleOfficeCode=office };
        var invoice = new LocalInvoice { Id=Guid.NewGuid(), CustomerId=customer.Id, TenantCode=tenant, OfficeCode=owner, ResponsibleOfficeCode=office, VoucherType=voucher, InvoiceDate=new DateOnly(2026,9,12), IsLatestVersion=true, VersionNumber=1 };
        invoice.VersionGroupId=invoice.Id;
        db.Customers.Add(customer); db.Invoices.Add(invoice); await db.SaveChangesAsync();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        using var vm = new SalesViewModel(local, null!, null!, session, voucher);
        var transitions = new List<Guid>();
        vm.PropertyChanged += (_, e) => { if(e.PropertyName==property.Name) transitions.Add((Guid)property.GetValue(vm)!); };
        Assert.NotEqual(Guid.Empty, vm.InvoiceId);
        Assert.Equal(Guid.Empty, property.GetValue(vm));
        await vm.LoadInvoiceAsync(invoice);
        Assert.Equal(invoice.Id, property.GetValue(vm));
        Assert.Equal(new[]{invoice.Id}, transitions);
        vm.NewInvoice();
        Assert.NotEqual(invoice.Id, vm.InvoiceId);
        Assert.Equal(Guid.Empty, property.GetValue(vm));
        Assert.Equal(new[]{invoice.Id,Guid.Empty}, transitions);
        Assert.Equal(invoice.Id, (await db.Invoices.AsNoTracking().SingleAsync()).Id);
        Assert.Equal(1, await db.Customers.CountAsync());
    }
}

