using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InvoiceScopeAndQuantityIntegrityTests
{
    [Theory]
    [InlineData("none")] [InlineData("sales")] [InlineData("purchase")] [InlineData("both")] [InlineData("admin")]
    public async Task TransactionPull_KindsAndMixedDirectionsPreserveScopeAndStorage(string permission)
    {
        var user = permission == "admin" ? new TestCurrentUserContext { IsAdmin = true } : TransactionReadUser(permission);
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var records = new List<TransactionRecord>();
        foreach (var kind in new[] { "일반수금", "전표수금", "선수금입금", "선수금환불", "선수금차감", "렌탈수금", "일반지급", "전표지급", "알수없음", "혼합" })
        {
            var purchase = kind is "일반지급" or "전표지급";
            var row = new TransactionRecord { CustomerId = customer.Id, TransactionKind = kind == "혼합" ? "일반수금" : kind,
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                Memo = kind, Note = "keep-note", SettlementAmount = 1234,
                CashReceipt = purchase || kind == "선수금환불" ? 0 : 1234,
                ReceiptTotal = purchase || kind == "선수금환불" ? 0 : 1234,
                CashPayment = purchase || kind is "선수금환불" or "혼합" ? 1234 : 0,
                PaymentTotal = purchase || kind is "선수금환불" or "혼합" ? 1234 : 0 };
            records.Add(row);
        }
        var otherOffice = new TransactionRecord { CustomerId = customer.Id, TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu, ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu, TransactionKind = "일반수금", CashReceipt = 55, ReceiptTotal = 55 };
        db.Add(customer); db.AddRange(records); db.Add(otherOffice); await db.SaveChangesAsync();
        var before = (await db.Transactions.AsNoTracking().ToListAsync()).ToDictionary(x => x.Id, x => JsonSerializer.Serialize(x.ToDto()));
        db.ChangeTracker.Clear();
        var pull = Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>((await CreateSyncController(db,user).Pull(0,CancellationToken.None)).Result).Value);
        // Amount grants and administrator flag do not widen this OfficeOnly scope.
        Assert.DoesNotContain(pull.Transactions,x => x.Id == otherOffice.Id);
        foreach (var row in records)
        {
            var dto = Assert.Single(pull.Transactions.Where(x => x.Id == row.Id));
            var expected = permission is "both" or "admin" || row.Memo is not ("알수없음" or "혼합") &&
                permission == (row.TransactionKind is "일반지급" or "전표지급" ? "purchase" : "sales");
            foreach (var money in typeof(TransactionDto).GetProperties().Where(p => p.PropertyType == typeof(decimal?)))
                Assert.Equal(expected ? money.GetValue(row.ToDto()) : null, money.GetValue(dto));
            Assert.Equal(row.Memo,dto.Memo); Assert.Equal(row.Note,dto.Note); Assert.Equal(row.Revision,dto.Revision);
            Assert.Equal(before[row.Id],JsonSerializer.Serialize((await db.Transactions.AsNoTracking().SingleAsync(x=>x.Id==row.Id)).ToDto()));
        }
    }

    [Theory]
    [InlineData("sales", VoucherType.Sales, true)] [InlineData("sales", VoucherType.Purchase, false)]
    [InlineData("purchase", VoucherType.Purchase, false)] [InlineData("sales", null, false)] [InlineData("both", null, true)]
    public async Task TransactionPull_LinkedInvoiceTypeMustConfirmDirection(string permission, VoucherType? type, bool allowed)
    {
        var user = TransactionReadUser(permission); await using var db=CreateDbContext(user);
        var customer=CreateCustomer(OfficeCodeCatalog.Usenet); var item=CreateItem(ItemTrackingTypes.NonStock,0);
        var invoice=CreateInvoice(Guid.NewGuid(),customer,item,1,ItemTrackingTypes.NonStock);
        if(type.HasValue) invoice.VoucherType=type.Value;
        db.AddRange(customer,item); if(type.HasValue)db.Add(invoice);
        var row=new TransactionRecord {CustomerId=customer.Id,LinkedInvoiceId=invoice.Id,TransactionKind="전표수금",ReceiptTotal=77,CashReceipt=77};
        db.Add(row);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        var pull=Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>((await CreateSyncController(db,user).Pull(0,CancellationToken.None)).Result).Value);
        Assert.Equal(allowed?77m:null,Assert.Single(pull.Transactions).ReceiptTotal);
        Assert.Equal(77m,(await db.Transactions.AsNoTracking().SingleAsync()).ReceiptTotal);
    }

    [Fact]
    public async Task TransactionPull_OmittedConflictMoneyIsExplicitlyUnknown()
    {
        var user=TransactionReadUser("none");await using var db=CreateDbContext(user);
        var conflict=new ConflictLogDto {EntityName="Transaction",ServerJson="{\"TransactionKind\":\"일반수금\",\"ReceiptTotal\":42,\"Memo\":\"keep\"}"};
        await TransactionAmountReadPolicy.ApplyConflictsAsync([conflict],db,new OfficeScopeService(user,db),CancellationToken.None);
        var dto=JsonSerializer.Deserialize<TransactionDto>(conflict.ServerJson)!;
        foreach(var p in typeof(TransactionDto).GetProperties().Where(p=>p.PropertyType==typeof(decimal?))) Assert.Null(p.GetValue(dto));
        Assert.Equal("keep",dto.Memo);
    }
}
