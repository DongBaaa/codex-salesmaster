using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InvoiceScopeAndQuantityIntegrityTests
{
    public static IEnumerable<object[]> ItemAmountCases()
    {
        foreach (var sync in new[] { false, true })
        foreach (var create in new[] { false, true })
        foreach (var mask in new[] { 0, 1, 2, 3, 4 })
            yield return [sync, create, mask];
    }

    [Theory]
    [MemberData(nameof(ItemAmountCases))]
    public async Task RestrictedItemAmounts_SaveNonMoneyAndPreservePricesByPermission(bool sync, bool create, int mask)
    {
        var user = ItemAmountUser(mask);
        await using var db = CreateDbContext(user);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0);
        item.PurchasePrice = 700;
        item.SalePrice = 1100;
        item.RetailPrice = 1200;
        item.PriceGradeA = 1000;
        item.PriceGradeB = 900;
        item.PriceGradeC = 800;
        if (!create)
        {
            db.Items.Add(item);
            await db.SaveChangesAsync();
        }
        var dto = item.ToDto();
        dto.ExpectedRevision = create ? 0 : item.Revision;
        dto.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
        dto.PurchasePrice = 1;
        dto.SalePrice = 2;
        dto.RetailPrice = 3;
        dto.PriceGradeA = 4;
        dto.PriceGradeB = 5;
        dto.PriceGradeC = 6;
        dto.SimpleMemo = "금액 외 품목 메모 저장";
        dto.MutationId = $"item-amount:{Guid.NewGuid():N}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;
        var json = JsonSerializer.Serialize(dto);
        await SavePricedItem(sync, create, db, user, dto);
        db.ChangeTracker.Clear();
        var saved = await db.Items.SingleAsync(x => x.Id == dto.Id);
        var sales = mask == 4 || (mask & 1) != 0;
        var purchase = mask == 4 || (mask & 2) != 0;
        Assert.Equal(purchase ? 1 : create ? 0 : 700, saved.PurchasePrice);
        Assert.Equal(sales ? 2 : create ? 0 : 1100, saved.SalePrice);
        Assert.Equal(sales ? 3 : create ? 0 : 1200, saved.RetailPrice);
        Assert.Equal(sales ? 4 : create ? 0 : 1000, saved.PriceGradeA);
        Assert.Equal(sales ? 5 : create ? 0 : 900, saved.PriceGradeB);
        Assert.Equal(sales ? 6 : create ? 0 : 800, saved.PriceGradeC);
        Assert.Equal(dto.SimpleMemo, saved.SimpleMemo);
        var revision = saved.Revision;
        await SavePricedItem(sync, create, db, user, JsonSerializer.Deserialize<ItemDto>(json)!, sync);
        db.ChangeTracker.Clear();
        Assert.Equal(revision, (await db.Items.SingleAsync(x => x.Id == dto.Id)).Revision);
        Assert.Single(await db.ProcessedSyncMutations.ToListAsync());
    }

    [Fact]
    public async Task RestrictedItemAmounts_SamePushCannotChangePriceThenUnderchargeInvoice()
    {
        var user = ItemAmountUser(0);
        await using var db = CreateDbContext(user);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0);
        item.SalePrice = 1100;
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        db.AddRange(item, customer);
        await db.SaveChangesAsync();
        var changedItem = item.ToDto();
        changedItem.ExpectedRevision = item.Revision;
        changedItem.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
        changedItem.SalePrice = 1;
        var invoice = BuildInvoiceDto(Guid.NewGuid(), customer, item,
            OfficeCodeCatalog.UsenetMainWarehouse, 3, ItemTrackingTypes.NonStock);
        invoice.Lines[0].UnitPrice = 1;
        invoice.Lines[0].LineAmount = 3;
        var response = await CreateSyncController(db, user).Push(new SyncPushRequest
        {
            DeviceId = "item-then-invoice", Items = [changedItem], Invoices = [invoice]
        }, CancellationToken.None);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Empty(result.Conflicts);
        Assert.Equal(2, result.AcceptedCount);
        db.ChangeTracker.Clear();
        Assert.Equal(1100, (await db.Items.SingleAsync()).SalePrice);
        Assert.Equal(3300, (await db.Invoices.SingleAsync()).TotalAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestrictedItemAmounts_PriceGradeWritesRequireSalesAmountPermission(bool settingOption)
    {
        var user = ItemAmountUser(2); // Purchase visibility does not authorize sales prices.
        await using var db = CreateDbContext(user);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0);
        var option = new PriceGradeOption { Name = "계약단가", PriceSource = "Sales" };
        db.AddRange(item, option);
        await db.SaveChangesAsync();
        var request = new SyncPushRequest { DeviceId = "price-grade-no-permission" };
        if (settingOption)
        {
            request.PriceGradeOptions.Add(new PriceGradeOptionDto
            {
                Id = option.Id, Name = option.Name, PriceSource = "B",
                ExpectedRevision = option.Revision, UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1)
            });
        }
        else
        {
            request.ItemPriceGrades.Add(new ItemPriceGradeDto
            {
                Id = Guid.NewGuid(), ItemId = item.Id, PriceGradeOptionId = option.Id,
                PriceGradeName = option.Name, UnitPrice = 1, IsActive = true,
                UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1)
            });
        }
        var response = await CreateSyncController(db, user).Push(request, CancellationToken.None);
        Assert.Equal(403, Assert.IsType<ObjectResult>(response.Result).StatusCode);
        db.ChangeTracker.Clear();
        Assert.Equal("Sales", (await db.PriceGradeOptions.SingleAsync()).PriceSource);
        Assert.Empty(await db.ItemPriceGrades.ToListAsync());
        Assert.Empty(await db.ProcessedSyncMutations.ToListAsync());
    }

    private static TestCurrentUserContext ItemAmountUser(int mask)
    {
        var permissions = new List<string> { PermissionNames.ItemEdit, PermissionNames.InvoiceEdit, PermissionNames.SettingsEdit };
        if ((mask & 1) != 0) permissions.Add(PermissionNames.AmountViewSales);
        if ((mask & 2) != 0) permissions.Add(PermissionNames.AmountViewPurchase);
        return new TestCurrentUserContext { Username = "item-amount-test", Permissions = permissions, IsAdmin = mask == 4 };
    }

    private static async Task SavePricedItem(bool sync, bool create, AppDbContext db,
        TestCurrentUserContext user, ItemDto dto, bool duplicate = false)
    {
        if (sync)
        {
            var response = await CreateSyncController(db, user).Push(new SyncPushRequest
            {
                DeviceId = "item-amount-tests", Items = [dto]
            }, CancellationToken.None);
            var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
            Assert.Empty(result.Conflicts);
            Assert.Equal(1, result.AcceptedCount);
            Assert.Equal(duplicate ? 1 : 0, result.DuplicateMutationCount);
        }
        else
        {
            var controller = new ItemsController(db, new OfficeScopeService(user, db));
            var response = create ? await controller.Create(dto, CancellationToken.None)
                : await controller.Update(dto.Id, dto, CancellationToken.None);
            Assert.IsType<OkObjectResult>(response.Result);
        }
    }
}
