using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    private static LocalDbContext CreateHiddenTransactionDb()
        => new(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(
            "Data Source=" + System.IO.Path.Combine(Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!, "transaction.db")).Options);

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task HiddenTransaction_RestartPreservesUnknownVersusZeroAndBlocksWrites(bool hidden, bool web)
    {
        PrepareAppRoot("hidden-transaction-roundtrip");
        try
        {
            var dbOptions = new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(
                "Data Source=" + System.IO.Path.Combine(Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT")!, "transaction.db")).Options;
            var options = new JsonSerializerOptions(web ? JsonSerializerDefaults.Web : JsonSerializerDefaults.General)
                { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            var dto = new TransactionDto { Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(),
                SettlementAmount = hidden ? null : 0m, Note = "비고 보존", Memo = "메모 보존", Revision = 7 };
            var json = JsonSerializer.Serialize(dto, options);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(hidden ? JsonValueKind.Null : JsonValueKind.Number,
                document.RootElement.GetProperty(web ? "settlementAmount" : "SettlementAmount").ValueKind);
            await using (var db = new LocalDbContext(dbOptions))
            {
                await db.Database.EnsureCreatedAsync();
                db.Transactions.Add(LocalMappings.ToLocal(JsonSerializer.Deserialize<TransactionDto>(json, options)!));
                await db.SaveChangesAsync();
            }
            await using (var db = new LocalDbContext(dbOptions))
            {
                var stored = await db.Transactions.SingleAsync();
                Assert.Equal(hidden, stored.AmountsHidden);
                Assert.False(stored.IsDirty);
                Assert.Equal(7, stored.Revision);
                var outgoing = LocalMappings.ToDto(stored);
                foreach (var amount in typeof(TransactionDto).GetProperties().Where(p => p.PropertyType == typeof(decimal?)))
                    Assert.Equal(hidden ? null : (object)0m, amount.GetValue(outgoing));
                Assert.Equal(dto.Note, outgoing.Note); Assert.Equal(dto.Memo, outgoing.Memo);
                if (hidden)
                {
                    var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), CreateAdminSession());
                    await Assert.ThrowsAsync<InvalidOperationException>(() => local.SaveTransactionAsync(stored));
                    Assert.False((await local.SaveTransactionAsync(stored, CreateAdminSession())).Success);
                    Assert.False(stored.IsDirty);
                    Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
                }
            }
        }
        finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
    }

    [Fact]
    public async Task HiddenTransaction_LegacyMigrationPreservesAmountRevisionAndDirty()
    {
        PrepareAppRoot("hidden-transaction-schema");
        try
        {
            await using var db = CreateHiddenTransactionDb();
            await db.Database.EnsureCreatedAsync();
            db.Transactions.Add(new LocalTransaction { Id = Guid.NewGuid(), ReceiptTotal = 2345m,
                CashReceipt = 2345m, IsDirty = true, Revision = 9 });
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Transactions DROP COLUMN AmountsHidden;");
            var migrate = typeof(LocalDbInitializer).GetMethod("MigrateColumnsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            await (Task)migrate.Invoke(null, [db])!; await (Task)migrate.Invoke(null, [db])!;
            var stored = await db.Transactions.SingleAsync();
            Assert.False(stored.AmountsHidden); Assert.Equal(2345m, stored.ReceiptTotal);
            Assert.Equal(2345m, stored.CashReceipt); Assert.True(stored.IsDirty); Assert.Equal(9, stored.Revision);
        }
        finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenTransaction_ReconciliationPreservesKnownPaymentAndDeletesOnlyTombstone(bool deleted)
    {
        PrepareAppRoot("hidden-transaction-reconcile");
        try
        {
            await using var db = CreateHiddenTransactionDb();
            await db.Database.EnsureCreatedAsync();
            var invoice = new LocalInvoice { Id = Guid.NewGuid(), TotalAmount = 3300m, IsDirty = false };
            var id = Guid.NewGuid();
            var payment = new LocalPayment { Id = id, InvoiceId = invoice.Id, Amount = 1100m, Revision = 4, IsDirty = false };
            db.AddRange(invoice, payment, new LocalTransaction { Id = id, LinkedInvoiceId = invoice.Id,
                AmountsHidden = true, IsDeleted = deleted, Revision = 7, IsDirty = false });
            await db.SaveChangesAsync();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), CreateAdminSession());
            await local.ReconcilePulledTransactionSideEffectsAsync([id]);
            await db.SaveChangesAsync(); db.ChangeTracker.Clear();
            var stored = await db.Payments.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(deleted, stored.IsDeleted); Assert.False(stored.IsDirty);
            Assert.Equal(1100m, stored.Amount); Assert.Equal(4, stored.Revision);
            Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
        }
        finally { Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null); SqliteConnection.ClearAllPools(); }
    }
}
