using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InvoiceScopeAndQuantityIntegrityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task HiddenInvoiceAmounts_RestrictedCreateCalculatesAndReplays_VisibleUserMustRefresh(
        bool sync, bool mayView)
    {
        var user = AmountUser(VoucherType.Sales, mayView);
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0);
        item.SalePrice = 1100;
        db.AddRange(customer, item);
        await db.SaveChangesAsync();
        var dto = BuildInvoiceDto(Guid.NewGuid(), customer, item,
            OfficeCodeCatalog.UsenetMainWarehouse, 3, ItemTrackingTypes.NonStock);
        dto.TotalAmount = dto.SupplyAmount = dto.VatAmount = null;
        dto.Lines[0].UnitPrice = dto.Lines[0].LineAmount = null;
        dto.MutationId = $"hidden-create:{Guid.NewGuid():N}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(dto);
        if (mayView)
        {
            if (sync)
            {
                var response = await CreateSyncController(db, user).Push(new SyncPushRequest
                { DeviceId = "amount-tests", Invoices = [dto] }, CancellationToken.None);
                var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
                Assert.Equal(0, result.AcceptedCount);
                Assert.Single(result.Conflicts);
            }
            else
            {
                var response = await CreateInvoicesController(db, user).Create(dto, CancellationToken.None);
                Assert.IsType<BadRequestObjectResult>(response.Result);
            }
            Assert.Empty(await db.Invoices.ToListAsync());
            Assert.Empty(await db.ProcessedSyncMutations.ToListAsync());
            return;
        }
        await SaveAmountInvoice(sync, db, user, dto, false);
        db.ChangeTracker.Clear();
        var saved = await db.Invoices.Include(x => x.Lines).SingleAsync();
        Assert.Equal(3300m, saved.TotalAmount);
        Assert.Equal(1100m, saved.Lines.Single().UnitPrice);
        var revision = saved.Revision;
        await SaveAmountInvoice(sync, db, user, JsonSerializer.Deserialize<InvoiceDto>(json)!, false, duplicate: sync);
        Assert.Equal(revision, (await db.Invoices.SingleAsync()).Revision);
        Assert.Single(await db.ProcessedSyncMutations.ToListAsync());
    }
}
