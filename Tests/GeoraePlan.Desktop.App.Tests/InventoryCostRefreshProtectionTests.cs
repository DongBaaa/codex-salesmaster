using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InventoryCostRefreshProtectionTests
{
    [Fact]
    public async Task IncompleteHistoryRetainsServerStockAndDirtyItem()
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);await db.Database.EnsureCreatedAsync();
        var id=Guid.NewGuid();
        db.Items.Add(new LocalItem {Id=id,NameOriginal="retained dirty item",CurrentStock=123,IsDirty=true,Revision=909});
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock {ItemId=id,WarehouseCode="USENET_MAIN",Quantity=123,Revision=910});await db.SaveChangesAsync();
        var session=new SessionState();session.SetOfflineSession(new UserSessionDto {Username="scope-test",Role="User",OfficeCode="USENET",TenantCode="USENET_GROUP",ScopeType=TenantScopeCatalog.ScopeOfficeOnly});
        var local=new LocalStateService(db,new OfficeAccessService(),new SyncRequestDispatcher(),session);
        var before=await Snapshot(connection);
        await using(var tx=await db.Database.BeginTransactionAsync()){await Refresh(local,false);await tx.CommitAsync();}
        var after=await Snapshot(connection);
        Assert.Equal(before["Items"],after["Items"]);Assert.Equal(before["ItemWarehouseStocks"],after["ItemWarehouseStocks"]);
        Assert.True((await db.Items.SingleAsync()).IsDirty);
    }

    [Theory]
    [InlineData("USENET", false)]
    [InlineData("USENET", true)]
    [InlineData("YEONSU", false)]
    [InlineData("YEONSU", true)]
    [InlineData("ITWORLD", false)]
    [InlineData("ITWORLD", true)]
    public async Task DerivedCostRefresh_PreservesAllOtherInvoiceFields(string office, bool dirty)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var id = Guid.NewGuid();
        var savedAt = new DateTime(2026, 8, 1, 2, 30, 0, DateTimeKind.Utc);
        var invoice = new LocalInvoice
        {
            Id = id, VersionGroupId = id, VersionNumber = 1, IsLatestVersion = true,
            IsConfirmed = true, VoucherType = VoucherType.Sales, InvoiceNumber = "LOCAL-PRESERVED",
            TenantCode = office == "ITWORLD" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
            OfficeCode = office, ResponsibleOfficeCode = office, SourceWarehouseCode = office + "_MAIN",
            InvoiceDate = DateOnly.FromDateTime(savedAt), Memo = "pending user edit",
            IsDirty = dirty, Revision = 919, CostStatus = "Pending", ConcurrencyStamp = "preserved-stamp",
            CreatedAtUtc = savedAt.AddDays(-1), UpdatedAtUtc = savedAt, LastSavedAtUtc = savedAt
        };
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        invoice = await db.Invoices.SingleAsync(row => row.Id == id);
        var before = db.Entry(invoice).CurrentValues.Clone();
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto {Username = "cost-metadata-test", Role = "User",
            OfficeCode = office, TenantCode = invoice.TenantCode, ScopeType = TenantScopeCatalog.ScopeOfficeOnly});
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var inventoryEvents = 0;
        local.InventoryStateChanged += (_, _) => inventoryEvents++;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using (var tx = await db.Database.BeginTransactionAsync())
            {
                await Refresh(local, true);
                await tx.CommitAsync();
            }
            db.ChangeTracker.Clear();
            var afterInvoice = await db.Invoices.SingleAsync(row => row.Id == id);
            Assert.Equal("Settled", afterInvoice.CostStatus);
            var after = db.Entry(afterInvoice).CurrentValues;
            foreach (var property in before.Properties.Where(property => property.Name != nameof(LocalInvoice.CostStatus)))
                Assert.Equal(before[property.Name], after[property.Name]);
            // The first refresh changes CostStatus. Repeating the same forced
            // calculation must not publish another UI change without a write.
            Assert.Equal(1, inventoryEvents);
        }
    }

    [Theory]
    [InlineData("USENET", false)]
    [InlineData("USENET", true)]
    [InlineData("YEONSU", false)]
    [InlineData("YEONSU", true)]
    [InlineData("ITWORLD", false)]
    [InlineData("ITWORLD", true)]
    public async Task DerivedCostRefresh_RollbackPreservesInvoiceAndAllowsRetry(string office, bool existingSignature)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var id = Guid.NewGuid();
        var stamp = new DateTime(2026, 8, 1, 2, 30, 0, DateTimeKind.Utc);
        db.Invoices.Add(new LocalInvoice
        {
            Id = id, VersionGroupId = id, VersionNumber = 1, IsLatestVersion = true,
            IsConfirmed = true, VoucherType = VoucherType.Sales, InvoiceNumber = "ROLLBACK-PRESERVED",
            TenantCode = office == "ITWORLD" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
            OfficeCode = office, ResponsibleOfficeCode = office, SourceWarehouseCode = office + "_MAIN",
            InvoiceDate = DateOnly.FromDateTime(stamp), IsDirty = true, Revision = 919,
            CostStatus = "Pending", ConcurrencyStamp = "original-stamp",
            CreatedAtUtc = stamp.AddDays(-1), UpdatedAtUtc = stamp, LastSavedAtUtc = stamp
        });
        if (existingSignature)
            db.Settings.Add(new LocalSetting { Key = "InventoryCostInputs.v1", Value = "stale-input-hash" });
        await db.SaveChangesAsync();
        var before = await Snapshot(connection);
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { Username = "rollback-test", Role = "User",
            OfficeCode = office, TenantCode = office == "ITWORLD" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly });
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await Refresh(local, true);
            Assert.Equal("Settled", await db.Invoices.AsNoTracking().Where(row => row.Id == id).Select(row => row.CostStatus).SingleAsync());
            await tx.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        var rolledBack = await Snapshot(connection);
        foreach (var table in before.Keys) Assert.Equal(before[table], rolledBack[table]);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            await Refresh(local, true);
            await tx.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var invoice = await db.Invoices.SingleAsync(row => row.Id == id);
        Assert.Equal("Settled", invoice.CostStatus);
        Assert.Equal(stamp, invoice.UpdatedAtUtc);
        Assert.True(invoice.IsDirty);
        Assert.Equal(919, invoice.Revision);
        Assert.Equal("original-stamp", invoice.ConcurrencyStamp);
    }

    private static async Task Refresh(LocalStateService local,bool force)
    {await (Task)typeof(LocalStateService).GetMethod("RefreshInventoryCostAfterPullAsync",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(local,[force,CancellationToken.None])!;}
    internal static async Task<Dictionary<string,string>> Snapshot(SqliteConnection connection)
    {
        var names=new List<string>();using var cmd=connection.CreateCommand();cmd.CommandText="select name from sqlite_master where type='table' and name not like 'sqlite_%'";
        using(var reader=await cmd.ExecuteReaderAsync())while(await reader.ReadAsync())names.Add(reader.GetString(0));
        var result=new Dictionary<string,string>();foreach(var name in names)result[name]=await Rows(connection,"SELECT * FROM \""+name+"\"");return result;
    }
    private static async Task<string> Rows(SqliteConnection connection,string query)
    {
        using var cmd=connection.CreateCommand();cmd.CommandText=query;using var reader=await cmd.ExecuteReaderAsync();var rows=new List<string>();
        while(await reader.ReadAsync()){var row=new object[reader.FieldCount];reader.GetValues(row);rows.Add(JsonSerializer.Serialize(row));}
        rows.Sort(StringComparer.Ordinal);return string.Join('\n',rows);
    }
}
