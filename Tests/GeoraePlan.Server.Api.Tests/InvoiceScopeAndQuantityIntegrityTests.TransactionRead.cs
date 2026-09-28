using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InvoiceScopeAndQuantityIntegrityTests
{
    public static IEnumerable<object[]> TransactionReadKinds()
    {
        foreach (var kind in new[] { "일반수금", "전표수금", "선수금입금", "선수금환불", "선수금차감", "렌탈수금", "일반지급", "전표지급", "알수없음", "" })
        foreach (var permission in new[] { "none", "sales", "purchase", "both" })
            yield return [kind, permission];
    }

    [Theory, MemberData(nameof(TransactionReadKinds))]
    public async Task TransactionRead_ConflictScopesUseKindAndKeepMetadata(string kind, string permission)
    {
        var user = TransactionReadUser(permission);
        await using var db = CreateDbContext(user);
        var dto = new TransactionDto { Id = Guid.NewGuid(), TransactionKind = kind,
            Memo = "keep-memo", Note = "keep-note", Revision = 17 };
        var money = typeof(TransactionDto).GetProperties().Where(p => p.PropertyType == typeof(decimal?)).ToArray();
        var isPurchase = kind is "일반지급" or "전표지급";
        if (isPurchase || kind == "선수금환불") dto.CashPayment = dto.PaymentTotal = 1234m;
        else dto.CashReceipt = dto.ReceiptTotal = 1234m;
        var raw = JsonSerializer.Serialize(dto);
        var conflict = new ConflictLogDto { EntityName = "TransactionRecord", ClientJson = raw, ServerJson = raw };
        await TransactionAmountReadPolicy.ApplyConflictsAsync([conflict], db, new OfficeScopeService(user, db), CancellationToken.None);
        var allowed = permission == "both" || kind != "알수없음" && kind != "" && permission == (isPurchase ? "purchase" : "sales");
        foreach (var json in new[] { conflict.ClientJson, conflict.ServerJson })
        {
            var wire = JsonSerializer.Deserialize<TransactionDto>(json)!;
            foreach (var p in money) Assert.Equal(allowed ? p.GetValue(dto) : null, p.GetValue(wire));
            Assert.Equal(dto.Id, wire.Id); Assert.Equal(17, wire.Revision);
            Assert.Equal("keep-memo", wire.Memo); Assert.Equal("keep-note", wire.Note);
        }
    }

    [Theory]
    [InlineData("sales", VoucherType.Sales, true)]
    [InlineData("sales", VoucherType.Purchase, false)]
    [InlineData("purchase", VoucherType.Purchase, false)]
    [InlineData("sales", null, false)]
    [InlineData("both", null, true)]
    public async Task TransactionRead_LinkedInvoiceMustConfirmScope(string permission, VoucherType? type, bool allowed)
    {
        var user = TransactionReadUser(permission);
        await using var db = CreateDbContext(user);
        var dto = new TransactionDto { LinkedInvoiceId = Guid.NewGuid(), TransactionKind = "전표수금", ReceiptTotal = 77m };
        if (type.HasValue)
        {
            var customer = CreateCustomer(OfficeCodeCatalog.Usenet); var item = CreateItem(ItemTrackingTypes.NonStock, 0);
            var invoice = CreateInvoice(dto.LinkedInvoiceId.Value, customer, item, 1, ItemTrackingTypes.NonStock);
            invoice.VoucherType = type.Value; db.AddRange(customer, item, invoice); await db.SaveChangesAsync();
        }
        var conflict = new ConflictLogDto { EntityName = "Transaction", ServerJson = JsonSerializer.Serialize(dto) };
        await TransactionAmountReadPolicy.ApplyConflictsAsync([conflict], db, new OfficeScopeService(user, db), CancellationToken.None);
        Assert.Equal(allowed ? 77m : null, JsonSerializer.Deserialize<TransactionDto>(conflict.ServerJson)!.ReceiptTotal);
    }

    [Theory]
    [InlineData("sales", false)]
    [InlineData("purchase", false)]
    [InlineData("both", true)]
    public async Task TransactionRead_MixedDirectionNeedsBothAmountGrants(string permission, bool allowed)
    {
        var user = TransactionReadUser(permission); await using var db = CreateDbContext(user);
        var dto = new TransactionDto { TransactionKind = "일반수금", ReceiptTotal = 110m, PaymentTotal = 220m };
        var conflict = new ConflictLogDto { EntityName = "TransactionRecord", ServerJson = JsonSerializer.Serialize(dto) };
        await TransactionAmountReadPolicy.ApplyConflictsAsync([conflict], db, new OfficeScopeService(user, db), CancellationToken.None);
        var wire = JsonSerializer.Deserialize<TransactionDto>(conflict.ServerJson)!;
        Assert.Equal(allowed ? 110m : null, wire.ReceiptTotal); Assert.Equal(allowed ? 220m : null, wire.PaymentTotal);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"SettlementAmount\":\"invalid\"}")]
    public async Task TransactionRead_UnparseableConflictCannotDiscloseMoney(string raw)
    {
        var user = TransactionReadUser("sales"); await using var db = CreateDbContext(user);
        var conflict = new ConflictLogDto { EntityName = "TransactionRecord", ClientJson = raw, ServerJson = raw };
        await TransactionAmountReadPolicy.ApplyConflictsAsync([conflict], db, new OfficeScopeService(user, db), CancellationToken.None);
        Assert.Empty(conflict.ClientJson); Assert.Empty(conflict.ServerJson);
    }

    [Fact]
    public async Task TransactionRead_CamelCaseAndNestedMoneyAreHidden()
    {
        var user = TransactionReadUser("none"); await using var db = CreateDbContext(user);
        var raw = "{\"transactionKind\":\"일반수금\",\"receiptTotal\":123,\"extra\":[{\"CashReceipt\":456}],\"Memo\":\"keep\"}";
        var conflict = new ConflictLogDto { EntityName = "TransactionRecord", ServerJson = raw };
        await TransactionAmountReadPolicy.ApplyConflictsAsync([conflict], db, new OfficeScopeService(user, db), CancellationToken.None);
        var json = JsonNode.Parse(conflict.ServerJson)!;
        Assert.Null(json["receiptTotal"]); Assert.Null(json["extra"]![0]!["CashReceipt"]);
        Assert.Equal("keep", json["Memo"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("purchase", false)]
    [InlineData("sales", true)]
    public async Task TransactionRead_ActualPushMasksConflictWithoutChangingStoredAudit(string permission, bool allowed)
    {
        var user = TransactionReadUser(permission); await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var entity = new TransactionRecord { CustomerId = customer.Id, TransactionKind = "일반수금", ReceiptTotal = 1234, CashReceipt = 1234, Memo = "keep" };
        db.AddRange(customer, entity); await db.SaveChangesAsync(); var revision = entity.Revision;
        var dto = entity.ToDto(); dto.ExpectedRevision = revision + 100; dto.MutationId = Guid.NewGuid().ToString("N");
        var response = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>((await CreateSyncController(db, user).Push(
            new SyncPushRequest { DeviceId = "transaction-conflict-read", Transactions = [dto] }, CancellationToken.None)).Result).Value);
        var conflict = Assert.Single(response.Conflicts);
        Assert.Equal(allowed ? 1234m : null, JsonSerializer.Deserialize<TransactionDto>(conflict.ServerJson)!.ReceiptTotal);
        Assert.Equal(allowed ? 1234m : null, JsonSerializer.Deserialize<TransactionDto>(conflict.ClientJson)!.ReceiptTotal);
        var audit = await db.ConflictLogs.AsNoTracking().SingleAsync();
        Assert.Equal(1234m, JsonSerializer.Deserialize<TransactionDto>(audit.ServerJson)!.ReceiptTotal);
        var stored = await db.Transactions.AsNoTracking().SingleAsync();
        Assert.Equal(revision, stored.Revision); Assert.Equal(1234m, stored.ReceiptTotal); Assert.Equal("keep", stored.Memo);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("purchase", false)]
    [InlineData("sales", true)]
    public async Task TransactionRead_OverSettlementErrorDoesNotRevealRestrictedBalance(string permission, bool allowed)
    {
        var user = TransactionReadUser(permission); await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet); var item = CreateItem(ItemTrackingTypes.NonStock, 0);
        var invoice = CreateInvoice(Guid.NewGuid(), customer, item, 1, ItemTrackingTypes.NonStock); invoice.TotalAmount = 330;
        db.AddRange(customer, item, invoice); await db.SaveChangesAsync();
        var dto = new TransactionDto { Id = Guid.NewGuid(), CustomerId = customer.Id, LinkedInvoiceId = invoice.Id,
            TransactionKind = "전표수금", SettlementAmount = 999, CashReceipt = 999, ReceiptTotal = 999, ExpectedRevision = invoice.Revision };
        var response = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>((await CreateSyncController(db, user).Push(
            new SyncPushRequest { DeviceId = "transaction-balance-read", Transactions = [dto] }, CancellationToken.None)).Result).Value);
        var conflict = Assert.Single(response.Conflicts);
        Assert.Contains("exceeds current outstanding", conflict.Reason);
        Assert.Equal(allowed, conflict.Reason.Contains("outstanding=330", StringComparison.Ordinal));
        Assert.Empty(await db.Transactions.ToListAsync());
    }

    private static TestCurrentUserContext TransactionReadUser(string permission)
        => new() { Username = "transaction-reader", Permissions = permission switch
        {
            "sales" => [PermissionNames.PaymentEdit, PermissionNames.AmountViewSales],
            "purchase" => [PermissionNames.PaymentEdit, PermissionNames.AmountViewPurchase],
            "both" => [PermissionNames.PaymentEdit, PermissionNames.AmountViewSales, PermissionNames.AmountViewPurchase],
            _ => [PermissionNames.PaymentEdit]
        } };
}
