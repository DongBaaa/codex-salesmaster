using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task HiddenPayment_RoundTripAndRestartPreserveUnknownVersusRealZero(bool hidden, bool web)
    {
        PrepareAppRoot("hidden-payment-roundtrip");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(
                "Data Source=" + System.IO.Path.Combine(Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!, "payment.db")).Options;
            var options = new JsonSerializerOptions(web ? JsonSerializerDefaults.Web : JsonSerializerDefaults.General)
                { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            var dto = new PaymentDto { Id=Guid.NewGuid(), InvoiceId=Guid.NewGuid(), Amount=hidden ? null : 0m,
                PaymentDate=new DateOnly(2026,9,24), Note="기록 유지", Revision=5 };
            var json = JsonSerializer.Serialize(dto, options);
            using var wire = JsonDocument.Parse(json);
            Assert.Equal(hidden ? JsonValueKind.Null : JsonValueKind.Number, wire.RootElement.GetProperty(web ? "amount" : "Amount").ValueKind);
            await using (var db = new LocalDbContext(dbOptions))
            {
                await db.Database.EnsureCreatedAsync();
                db.Invoices.Add(new LocalInvoice { Id=dto.InvoiceId, TotalAmount=1000 });
                db.Payments.Add(LocalMappings.ToLocal(JsonSerializer.Deserialize<PaymentDto>(json,options)!));
                await db.SaveChangesAsync();
            }
            await using (var db = new LocalDbContext(dbOptions))
            {
                var stored = await db.Payments.SingleAsync();
                Assert.Equal(hidden,stored.AmountsHidden);
                Assert.Equal(0m,stored.Amount);
                var sent=LocalMappings.ToDto(stored);
                Assert.Equal(dto.Amount,sent.Amount);
                Assert.Equal(dto.Note,sent.Note);
                var invoice=await db.Invoices.Include(x=>x.Payments).SingleAsync();
                var row=InvoiceListRow.From(invoice,"거래처",true);
                Assert.Equal(hidden,row.AmountsHidden);
                Assert.Equal(hidden ? "비공개" : "1,000",row.TotalAmountDisplay);
                if(hidden)
                {
                    var local=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),CreateAdminSession());
                    await Assert.ThrowsAsync<InvalidOperationException>(()=>local.SavePaymentAsync(stored));
                    Assert.False((await local.SavePaymentAsync(stored,CreateAdminSession())).Success);
                    Assert.False(stored.IsDirty);
                }
            }
        }
        finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT",null); SqliteConnection.ClearAllPools(); }
    }

    [Fact]
    public async Task HiddenPayment_LegacySchemaUpgradePreservesExistingAmountAndDirty()
    {
        PrepareAppRoot("hidden-payment-schema");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(
                "Data Source=" + System.IO.Path.Combine(Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!, "payment.db")).Options;
            await using var db=new LocalDbContext(dbOptions);
            await db.Database.EnsureCreatedAsync();
            var invoice=new LocalInvoice {Id=Guid.NewGuid(),TotalAmount=5000};
            db.Invoices.Add(invoice);
            db.Payments.Add(new LocalPayment {Id=Guid.NewGuid(),InvoiceId=invoice.Id,Amount=2345,IsDirty=true,Revision=9});
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Payments DROP COLUMN AmountsHidden;");
            var migrate=typeof(LocalDbInitializer).GetMethod("MigrateColumnsAsync",BindingFlags.NonPublic|BindingFlags.Static)!;
            await (Task)migrate.Invoke(null,[db])!; await (Task)migrate.Invoke(null,[db])!;
            var stored=await db.Payments.SingleAsync();
            Assert.False(stored.AmountsHidden); Assert.Equal(2345,stored.Amount); Assert.True(stored.IsDirty); Assert.Equal(9,stored.Revision);
        }
        finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT",null); SqliteConnection.ClearAllPools(); }
    }

    [Fact]
    public void HiddenPayment_MakesParentInvoiceUnknownButDeletedHistoryDoesNot()
    {
        var payment=new PaymentDto {Amount=null};
        var invoice=new InvoiceDto {TotalAmount=1000,Payments=[payment]};
        Assert.True(invoice.AmountsHidden);
        var local=LocalMappings.ToLocal(invoice);
        Assert.True(local.AmountsHidden);
        Assert.Null(LocalMappings.ToDto(local).Payments[0].Amount);
        payment.IsDeleted=true;
        Assert.False(invoice.AmountsHidden);
    }
}
