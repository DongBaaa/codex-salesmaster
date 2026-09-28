using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InvoiceScopeAndQuantityIntegrityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task MobileHiddenDraft_ReeditSnapshot_RetainsNegotiatedPriceAndReplays(bool sync, bool registered)
    {
        var user = AmountUser(VoucherType.Sales, false);
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0); item.SalePrice = 9900;
        var invoice = CreateInvoice(Guid.NewGuid(), customer, item, 1, ItemTrackingTypes.NonStock);
        invoice.VatMode = InvoiceVatModes.None;
        invoice.TotalAmount = invoice.SupplyAmount = 100;
        var original = invoice.Lines.Single();
        if (!registered) original.ItemId = null;
        original.ItemNameOriginal = "계약 당시 품목"; original.SpecificationOriginal = "계약 규격";
        original.Unit = "BOX"; original.SerialNumber = "원일련번호"; original.MaterialNumber = "원자재번호";
        original.InstallLocation = "기존 설치장소";
        original.RentalStartDate = new DateOnly(2025, 1, 2); original.RentalEndDate = new DateOnly(2027, 3, 4);
        db.AddRange(customer, item, invoice); await db.SaveChangesAsync();
        var dto = invoice.ToDto(); dto.ExpectedRevision = invoice.Revision;
        var mobile = InvoiceLineDraftItem.FromDto(dto.Lines.Single(), forceHideAmounts: true);
        var selection = registered ? item.ToDto() : new ItemDto { NameOriginal = mobile.ItemNameOriginal };
        var edited = InvoiceLineDraftItem.FromEditor(selection, mobile, 5, 0, "수량·비고만 변경", amountsHidden: true);
        dto.Lines = [edited.ToDto(dto.Id)];
        dto.TotalAmount = dto.SupplyAmount = dto.VatAmount = null;
        dto.MutationId = $"mobile-reedit:{Guid.NewGuid():N}";
        dto.MutationCreatedAtUtc = dto.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
        var queued = JsonSerializer.Serialize(dto);
        Assert.Null(dto.Lines.Single().UnitPrice);
        await SaveAmountInvoice(sync, db, user, JsonSerializer.Deserialize<InvoiceDto>(queued)!, true);
        db.ChangeTracker.Clear();
        var saved = await db.Invoices.Include(x => x.Lines).SingleAsync();
        var savedLine = saved.Lines.Single();
        Assert.Equal(500, saved.TotalAmount); Assert.Equal(100, savedLine.UnitPrice);
        Assert.Equal(original.Id, savedLine.Id); Assert.Equal(original.ItemId, savedLine.ItemId);
        Assert.Equal(original.ItemNameOriginal, savedLine.ItemNameOriginal);
        Assert.Equal(original.SpecificationOriginal, savedLine.SpecificationOriginal); Assert.Equal(original.Unit, savedLine.Unit);
        Assert.Equal(original.SerialNumber, savedLine.SerialNumber); Assert.Equal(original.MaterialNumber, savedLine.MaterialNumber);
        Assert.Equal(original.InstallLocation, savedLine.InstallLocation);
        Assert.Equal(original.RentalStartDate, savedLine.RentalStartDate); Assert.Equal(original.RentalEndDate, savedLine.RentalEndDate);
        Assert.Equal("수량·비고만 변경", savedLine.Remark);
        var revision = saved.Revision;
        await SaveAmountInvoice(sync, db, user, JsonSerializer.Deserialize<InvoiceDto>(queued)!, true, duplicate: sync);
        db.ChangeTracker.Clear();
        Assert.Equal(revision, (await db.Invoices.SingleAsync()).Revision);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MobileHiddenDraft_OfflineReplayAndQuantityEdit_UseServerPrice(bool sync, bool edit)
    {
        var user = AmountUser(VoucherType.Sales, false);
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0); item.SalePrice = 1100;
        db.AddRange(customer, item); await db.SaveChangesAsync();
        var dto = BuildInvoiceDto(Guid.NewGuid(), customer, item, OfficeCodeCatalog.UsenetMainWarehouse, 3, ItemTrackingTypes.NonStock);
        var mobile = InvoiceLineDraftItem.FromItem(item.ToDto(), 3, amountsHidden: true);
        dto.Lines = [mobile.ToDto(dto.Id)];
        dto.TotalAmount = dto.SupplyAmount = dto.VatAmount = null;
        dto.MutationId = $"mobile-hidden:{Guid.NewGuid():N}"; dto.MutationCreatedAtUtc = DateTime.UtcNow;
        var queued = JsonSerializer.Serialize(dto); // Capture wire bytes before the in-process controller normalizes its copy.
        await SaveAmountInvoice(sync, db, user, dto, false);
        if (edit)
        {
            db.ChangeTracker.Clear();
            var saved = await db.Invoices.Include(x => x.Lines).Include(x => x.Payments).SingleAsync();
            dto = saved.ToDto(); dto.ExpectedRevision = saved.Revision;
            mobile = InvoiceLineDraftItem.FromDto(dto.Lines.Single(), forceHideAmounts: true);
            mobile.Quantity = 5; mobile.Remark = "모바일 수량·비고 수정";
            dto.Lines = [mobile.ToDto(dto.Id)];
            dto.TotalAmount = dto.SupplyAmount = dto.VatAmount = null;
            dto.MutationId = $"mobile-hidden-edit:{Guid.NewGuid():N}";
            dto.MutationCreatedAtUtc = dto.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
            queued = JsonSerializer.Serialize(dto);
        }
        await SaveAmountInvoice(sync, db, user, JsonSerializer.Deserialize<InvoiceDto>(queued)!, edit, duplicate: sync && !edit);
        db.ChangeTracker.Clear();
        var final = await db.Invoices.Include(x => x.Lines).SingleAsync();
        Assert.Equal(edit ? 5500 : 3300, final.TotalAmount);
        Assert.Equal(1100, final.Lines.Single().UnitPrice);
        if (edit) Assert.Equal("모바일 수량·비고 수정", final.Lines.Single().Remark);
        var revision = final.Revision;
        await SaveAmountInvoice(sync, db, user, JsonSerializer.Deserialize<InvoiceDto>(queued)!, edit, duplicate: sync);
        db.ChangeTracker.Clear();
        Assert.Equal(revision, (await db.Invoices.SingleAsync()).Revision);
    }
}
