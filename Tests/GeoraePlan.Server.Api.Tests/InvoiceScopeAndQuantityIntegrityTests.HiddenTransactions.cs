using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InvoiceScopeAndQuantityIntegrityTests
{
    public static IEnumerable<object[]> HiddenTransactionFields()
        => typeof(TransactionDto).GetProperties().Where(p => p.PropertyType == typeof(decimal?))
            .SelectMany(p => new[] { new object[] { p.Name, false }, new object[] { p.Name, true } });

    [Theory, MemberData(nameof(HiddenTransactionFields))]
    public async Task HiddenTransaction_PushRejectsAnyUnknownMoneyWithoutMutatingStoredState(string field, bool update)
    {
        var user = CreateAdminUser();
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var entity = new TransactionRecord { Id = Guid.NewGuid(), CustomerId = customer.Id,
            TransactionKind = "일반수금", CashReceipt = 1100m, ReceiptTotal = 1100m, Note = "keep" };
        db.Add(customer);
        if (update) db.Add(entity);
        await db.SaveChangesAsync();
        var revision = entity.Revision;
        var dto = entity.ToDto(); dto.ExpectedRevision = revision; dto.Note = "must-not-persist";
        typeof(TransactionDto).GetProperty(field)!.SetValue(dto, null);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>((await CreateSyncController(db, user).Push(
            new SyncPushRequest { DeviceId = "hidden-transaction", Transactions = [dto] }, CancellationToken.None)).Result).Value);
        Assert.Contains(result.Conflicts, c => c.EntityId == entity.Id.ToString() && c.Reason.Contains("비공개"));
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Payments.IgnoreQueryFilters().ToListAsync());
        if (update)
        {
            var stored = await db.Transactions.IgnoreQueryFilters().SingleAsync();
            Assert.Equal(1100m, stored.CashReceipt); Assert.Equal(1100m, stored.ReceiptTotal);
            Assert.Equal("keep", stored.Note); Assert.Equal(revision, stored.Revision);
        }
        else Assert.Empty(await db.Transactions.IgnoreQueryFilters().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenTransaction_DeleteUsesStoredAmountsOrAcknowledgesMissingRow(bool exists)
    {
        var user = CreateAdminUser();
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var entity = new TransactionRecord { Id = Guid.NewGuid(), CustomerId = customer.Id,
            TransactionKind = "일반수금", CashReceipt = 1100m, ReceiptTotal = 1100m, Note = "keep" };
        db.Add(customer); if (exists) db.Add(entity); await db.SaveChangesAsync();
        var dto = entity.ToDto(); dto.ExpectedRevision = entity.Revision; dto.IsDeleted = true;
        foreach (var p in typeof(TransactionDto).GetProperties().Where(p => p.PropertyType == typeof(decimal?)))
            p.SetValue(dto, null);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>((await CreateSyncController(db, user).Push(
            new SyncPushRequest { DeviceId = "hidden-transaction-delete", Transactions = [dto] }, CancellationToken.None)).Result).Value);
        Assert.Empty(result.Conflicts);
        Assert.Contains(result.AcceptedRevisions, r => r.EntityId == entity.Id && r.IsDeleted == true);
        db.ChangeTracker.Clear();
        if (exists)
        {
            var stored = await db.Transactions.IgnoreQueryFilters().SingleAsync();
            Assert.True(stored.IsDeleted); Assert.Equal(1100m, stored.CashReceipt); Assert.Equal(1100m, stored.ReceiptTotal);
        }
        else Assert.Empty(await db.Transactions.IgnoreQueryFilters().ToListAsync());
    }
}
