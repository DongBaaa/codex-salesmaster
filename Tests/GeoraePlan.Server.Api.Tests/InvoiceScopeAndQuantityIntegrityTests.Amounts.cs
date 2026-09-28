using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InvoiceScopeAndQuantityIntegrityTests
{
    [Theory]
    [InlineData(false, VoucherType.Sales, false)]
    [InlineData(true, VoucherType.Sales, false)]
    [InlineData(false, VoucherType.Purchase, false)]
    [InlineData(true, VoucherType.Purchase, false)]
    [InlineData(false, VoucherType.Sales, true)]
    [InlineData(true, VoucherType.Purchase, true)]
    public async Task RestrictedAmounts_CreateUsesServerPrice_AndExactReplayDoesNotReprice(
        bool sync, VoucherType type, bool mayViewAmount)
    {
        var user = AmountUser(type, mayViewAmount);
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0);
        item.SalePrice = 1100;
        item.PurchasePrice = 700;
        db.AddRange(customer, item);
        await db.SaveChangesAsync();
        var dto = BuildInvoiceDto(Guid.NewGuid(), customer, item,
            OfficeCodeCatalog.UsenetMainWarehouse, 3, ItemTrackingTypes.NonStock);
        dto.VoucherType = type;
        dto.VatMode = InvoiceVatModes.Included;
        dto.Lines[0].UnitPrice = 2;
        dto.Lines[0].LineAmount = 6;
        dto.TotalAmount = 999999;
        dto.Memo = "수량과 비고 저장";
        dto.MutationId = $"amount-create:{Guid.NewGuid():N}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;
        var originalJson = JsonSerializer.Serialize(dto);
        await SaveAmountInvoice(sync, db, user, dto, false);
        db.ChangeTracker.Clear();
        var saved = await db.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == dto.Id);
        var expectedPrice = mayViewAmount ? 2 : type == VoucherType.Sales ? 1100 : 700;
        Assert.Equal(expectedPrice, Assert.Single(saved.Lines).UnitPrice);
        Assert.Equal(expectedPrice * 3, saved.TotalAmount);
        Assert.Equal(dto.Memo, saved.Memo);
        var revision = saved.Revision;
        var storedItem = await db.Items.SingleAsync(x => x.Id == item.Id);
        storedItem.SalePrice = 4400;
        storedItem.PurchasePrice = 5500;
        await db.SaveChangesAsync();
        await SaveAmountInvoice(sync, db, user, JsonSerializer.Deserialize<InvoiceDto>(originalJson)!, false, duplicate: sync);
        db.ChangeTracker.Clear();
        saved = await db.Invoices.SingleAsync(x => x.Id == dto.Id);
        Assert.Equal(revision, saved.Revision);
        Assert.Equal(expectedPrice * 3, saved.TotalAmount);
        Assert.Single(await db.ProcessedSyncMutations.ToListAsync());

        var tampered = JsonSerializer.Deserialize<InvoiceDto>(originalJson)!;
        tampered.Lines[0].Quantity = 4;
        if (sync)
        {
            var replay = await CreateSyncController(db, user).Push(new SyncPushRequest
            {
                DeviceId = "amount-tests", Invoices = [tampered]
            }, CancellationToken.None);
            var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(replay.Result).Value);
            Assert.Equal(0, result.AcceptedCount);
            Assert.Single(result.Conflicts);
        }
        else
        {
            var replay = await CreateInvoicesController(db, user).Create(tampered, CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(replay.Result);
        }
        db.ChangeTracker.Clear();
        Assert.Equal(revision, (await db.Invoices.SingleAsync(x => x.Id == dto.Id)).Revision);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RestrictedAmounts_UpdatePreservesNegotiatedPrice_AndIgnoresIncomingMoney(
        bool sync, bool changeQuantity)
    {
        var user = AmountUser(VoucherType.Sales, false);
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0);
        item.SalePrice = 9900;
        var invoice = CreateInvoice(Guid.NewGuid(), customer, item, 2, ItemTrackingTypes.NonStock);
        invoice.VatMode = InvoiceVatModes.Included;
        invoice.Lines.Single().UnitPrice = 1100;
        invoice.Lines.Single().LineAmount = 1980; // Previously negotiated line discount.
        invoice.TotalAmount = 1980;
        invoice.SupplyAmount = 1800;
        invoice.VatAmount = 180;
        db.AddRange(customer, item, invoice);
        await db.SaveChangesAsync();
        var dto = invoice.ToDto();
        dto.ExpectedRevision = invoice.Revision;
        dto.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
        dto.MutationId = $"amount-update:{Guid.NewGuid():N}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;
        dto.Memo = "비고 수정";
        dto.Lines[0].Remark = "행 비고 수정";
        dto.Lines[0].UnitPrice = 0;
        dto.Lines[0].LineAmount = 0;
        dto.TotalAmount = dto.SupplyAmount = dto.VatAmount = 0;
        if (changeQuantity) dto.Lines[0].Quantity = 3;
        await SaveAmountInvoice(sync, db, user, dto, true);
        db.ChangeTracker.Clear();
        var saved = await db.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == invoice.Id);
        Assert.Equal(1100, Assert.Single(saved.Lines).UnitPrice);
        Assert.Equal(changeQuantity ? 3300 : 1980, saved.TotalAmount);
        Assert.Equal(changeQuantity ? 3000 : 1800, saved.SupplyAmount);
        Assert.Equal(changeQuantity ? 300 : 180, saved.VatAmount);
        Assert.Equal(dto.Memo, saved.Memo);
        Assert.Equal("행 비고 수정", saved.Lines.Single().Remark);
    }

    [Theory]
    [InlineData(false, "custom", 850)]
    [InlineData(true, "custom", 850)]
    [InlineData(false, "configured", 750)]
    [InlineData(true, "configured", 750)]
    [InlineData(false, "legacy", 650)]
    [InlineData(true, "legacy", 650)]
    [InlineData(false, "vendor", 550)]
    [InlineData(true, "vendor", 550)]
    public async Task RestrictedAmounts_UsesCustomerPriceRules_AndScopedPurchaseHistory(
        bool sync, string source, decimal expectedPrice)
    {
        var user = AmountUser(VoucherType.Sales, false);
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        customer.PriceGrade = source == "legacy" ? "A_단가 적용" : "계약단가";
        var item = CreateItem(ItemTrackingTypes.NonStock, 0);
        item.SalePrice = 1100;
        item.PurchasePrice = 900;
        item.PriceGradeA = 650;
        item.PriceGradeB = 750;
        db.AddRange(customer, item);
        if (source is "custom" or "configured")
        {
            var grade = new PriceGradeOption { Name = customer.PriceGrade, PriceSource = "B" };
            db.Add(grade);
            if (source == "custom") db.Add(new ItemPriceGrade
            {
                ItemId = item.Id, PriceGradeOptionId = grade.Id, PriceGradeName = grade.Name, UnitPrice = 850
            });
        }
        if (source == "vendor")
        {
            var history = CreateInvoice(Guid.NewGuid(), customer, item, 1, ItemTrackingTypes.NonStock);
            history.VoucherType = VoucherType.Purchase;
            history.Lines.Single().UnitPrice = 550;
            db.Add(history);
            var otherOffice = CreateInvoice(Guid.NewGuid(), customer, item, 1, ItemTrackingTypes.NonStock);
            otherOffice.VoucherType = VoucherType.Purchase;
            otherOffice.OfficeCode = otherOffice.ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu;
            otherOffice.InvoiceDate = history.InvoiceDate.AddDays(1);
            otherOffice.Lines.Single().UnitPrice = 99999;
            db.Add(otherOffice);
        }
        await db.SaveChangesAsync();
        var dto = BuildInvoiceDto(Guid.NewGuid(), customer, item,
            OfficeCodeCatalog.UsenetMainWarehouse, 2, ItemTrackingTypes.NonStock);
        if (source == "vendor") dto.VoucherType = VoucherType.Purchase;
        await SaveAmountInvoice(sync, db, user, dto, false);
        db.ChangeTracker.Clear();
        var saved = await db.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == dto.Id);
        Assert.Equal(expectedPrice, saved.Lines.Single().UnitPrice);
        Assert.Equal(expectedPrice * 2, saved.TotalAmount);
    }

    [Theory]
    [InlineData(false, "unpriced")]
    [InlineData(true, "unpriced")]
    [InlineData(false, "unregistered")]
    [InlineData(true, "unregistered")]
    [InlineData(false, "unit")]
    [InlineData(true, "unit")]
    [InlineData(false, "overflow")]
    [InlineData(true, "overflow")]
    public async Task RestrictedAmounts_RejectsUnresolvablePrice_WithoutInvoiceStockOrReceiptMutation(bool sync, string reason)
    {
        var user = AmountUser(VoucherType.Sales, false);
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.Stock, 10);
        item.SalePrice = reason == "unpriced" ? 0 : reason == "overflow" ? 9000000000000000m : 1100;
        db.AddRange(customer, item);
        await db.SaveChangesAsync();
        var dto = BuildInvoiceDto(Guid.NewGuid(), customer, item,
            OfficeCodeCatalog.UsenetMainWarehouse, 2, ItemTrackingTypes.Stock);
        dto.MutationId = $"amount-rejected:{Guid.NewGuid():N}";
        dto.MutationCreatedAtUtc = DateTime.UtcNow;
        if (reason == "unregistered")
        {
            dto.Lines[0].ItemId = null;
            dto.Lines[0].ItemTrackingType = ItemTrackingTypes.NonStock;
        }
        if (reason == "unit") dto.Lines[0].Unit = "BOX";
        if (sync)
        {
            var response = await CreateSyncController(db, user).Push(new SyncPushRequest
            {
                DeviceId = "amount-tests", Invoices = [dto]
            }, CancellationToken.None);
            var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
            Assert.Equal(0, result.AcceptedCount);
            Assert.Single(result.Conflicts);
        }
        else
        {
            var response = await CreateInvoicesController(db, user).Create(dto, CancellationToken.None);
            Assert.IsType<BadRequestObjectResult>(response.Result);
        }
        db.ChangeTracker.Clear();
        Assert.Empty(await db.Invoices.ToListAsync());
        Assert.Empty(await db.InvoiceLines.ToListAsync());
        Assert.Empty(await db.ProcessedSyncMutations.ToListAsync());
        Assert.Empty(await db.InventoryLedgerEntries.ToListAsync());
        Assert.Equal(10, (await db.Items.SingleAsync()).CurrentStock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestrictedAmounts_SyncNewVersionWithNewLineIds_PreservesNegotiatedAmounts(bool changeQuantity)
    {
        var user = AmountUser(VoucherType.Sales, false);
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0);
        var old = CreateInvoice(Guid.NewGuid(), customer, item, 2, ItemTrackingTypes.NonStock);
        old.TotalAmount = old.SupplyAmount = 150;
        old.VatMode = InvoiceVatModes.None;
        old.Lines.Single().LineAmount = 150;
        db.AddRange(customer, item, old);
        await db.SaveChangesAsync();
        var dto = old.ToDto();
        dto.Id = Guid.NewGuid();
        dto.PreviousVersionId = old.Id;
        dto.VersionNumber = 2;
        dto.ExpectedRevision = old.Revision;
        dto.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
        dto.Lines[0].Id = Guid.NewGuid();
        dto.Lines[0].InvoiceId = dto.Id;
        dto.Lines[0].UnitPrice = dto.Lines[0].LineAmount = 0;
        dto.TotalAmount = dto.SupplyAmount = dto.VatAmount = 0;
        if (changeQuantity) dto.Lines[0].Quantity = 3;
        dto.Memo = "수정 버전";
        await SaveAmountInvoice(true, db, user, dto, false);
        db.ChangeTracker.Clear();
        var saved = await db.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == dto.Id);
        Assert.Equal(changeQuantity ? 300 : 150, saved.TotalAmount);
        Assert.Equal(100, saved.Lines.Single().UnitPrice);
        var previous = await db.Invoices.SingleAsync(x => x.Id == old.Id);
        Assert.Equal(150, previous.TotalAmount);
        Assert.False(previous.IsLatestVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestrictedAmounts_PaidInvoiceHeaderMemoEdit_DoesNotChangeMoney(bool sync)
    {
        var user = AmountUser(VoucherType.Sales, false);
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0);
        var invoice = CreateInvoice(Guid.NewGuid(), customer, item, 1, ItemTrackingTypes.NonStock);
        invoice.TotalAmount = 100;
        invoice.SupplyAmount = 91;
        invoice.VatAmount = 9;
        var payment = new Payment { InvoiceId = invoice.Id, Amount = 50 };
        db.AddRange(customer, item, invoice, payment);
        await db.SaveChangesAsync();
        var dto = invoice.ToDto();
        dto.ExpectedRevision = invoice.Revision;
        dto.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
        dto.Memo = "수금 전표 비고";
        dto.Lines[0].UnitPrice = dto.Lines[0].LineAmount = 0;
        dto.TotalAmount = dto.SupplyAmount = dto.VatAmount = 0;
        dto.Payments.Clear();
        await SaveAmountInvoice(sync, db, user, dto, true);
        db.ChangeTracker.Clear();
        var saved = await db.Invoices.SingleAsync(x => x.Id == invoice.Id);
        Assert.Equal(100, saved.TotalAmount);
        Assert.Equal(91, saved.SupplyAmount);
        Assert.Equal(dto.Memo, saved.Memo);
        Assert.Equal(50, (await db.Payments.SingleAsync()).Amount);
    }

    [Theory]
    [InlineData(VoucherType.Sales, PermissionNames.AmountViewPurchase)]
    [InlineData(VoucherType.Collection, PermissionNames.AmountViewPurchase)]
    [InlineData(VoucherType.Purchase, PermissionNames.AmountViewSales)]
    [InlineData(VoucherType.Procurement, PermissionNames.AmountViewSales)]
    [InlineData(VoucherType.Expense, PermissionNames.AmountViewSales)]
    public async Task RestrictedAmounts_OppositePermissionDoesNotAllowClientMoney(VoucherType type, string permission)
    {
        var user = new TestCurrentUserContext { Username = "opposite-amount", Permissions = [PermissionNames.InvoiceEdit, permission] };
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0);
        item.SalePrice = 1100;
        item.PurchasePrice = 700;
        db.AddRange(customer, item);
        await db.SaveChangesAsync();
        var dto = BuildInvoiceDto(Guid.NewGuid(), customer, item,
            OfficeCodeCatalog.UsenetMainWarehouse, 1, ItemTrackingTypes.NonStock);
        dto.VoucherType = type;
        dto.Lines[0].UnitPrice = 1;
        dto.Lines[0].LineAmount = 1;
        await SaveAmountInvoice(false, db, user, dto, false);
        db.ChangeTracker.Clear();
        var expected = type is VoucherType.Purchase or VoucherType.Procurement or VoucherType.Expense ? 700 : 1100;
        Assert.Equal(expected, (await db.Invoices.SingleAsync()).TotalAmount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestrictedAmounts_NewSameItemRowBeforeExisting_DoesNotTakeItsNegotiatedPrice(bool sync)
    {
        var user = AmountUser(VoucherType.Sales, false);
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer(OfficeCodeCatalog.Usenet);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0);
        item.SalePrice = 1100;
        var invoice = CreateInvoice(Guid.NewGuid(), customer, item, 1, ItemTrackingTypes.NonStock);
        invoice.VatMode = InvoiceVatModes.None;
        invoice.TotalAmount = invoice.SupplyAmount = 100;
        db.AddRange(customer, item, invoice);
        await db.SaveChangesAsync();
        var dto = invoice.ToDto();
        dto.ExpectedRevision = invoice.Revision;
        dto.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
        var added = JsonSerializer.Deserialize<InvoiceLineDto>(JsonSerializer.Serialize(dto.Lines[0]))!;
        added.Id = Guid.NewGuid();
        added.UnitPrice = added.LineAmount = 0;
        dto.Lines.Insert(0, added);
        await SaveAmountInvoice(sync, db, user, dto, true);
        db.ChangeTracker.Clear();
        var saved = await db.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == dto.Id);
        Assert.Equal(1100, saved.Lines.Single(x => x.Id == added.Id).UnitPrice);
        Assert.Equal(100, saved.Lines.Single(x => x.Id != added.Id).UnitPrice);
        Assert.Equal(1200, saved.TotalAmount);
    }

    private static TestCurrentUserContext AmountUser(VoucherType type, bool mayViewAmount) => new()
    {
        Username = "amount-test",
        Permissions = mayViewAmount
            ? [PermissionNames.InvoiceEdit, type == VoucherType.Sales ? PermissionNames.AmountViewSales : PermissionNames.AmountViewPurchase]
            : [PermissionNames.InvoiceEdit]
    };

    private static async Task SaveAmountInvoice(bool sync, 거래플랜.Server.Api.Data.AppDbContext db,
        TestCurrentUserContext user, InvoiceDto dto, bool update, bool duplicate = false)
    {
        if (sync)
        {
            var response = await CreateSyncController(db, user).Push(new SyncPushRequest
            {
                DeviceId = "amount-tests", Invoices = [dto]
            }, CancellationToken.None);
            var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
            Assert.Empty(result.Conflicts);
            Assert.Equal(1, result.AcceptedCount);
            Assert.Equal(duplicate ? 1 : 0, result.DuplicateMutationCount);
        }
        else
        {
            var controller = CreateInvoicesController(db, user);
            var response = update
                ? await controller.Update(dto.Id, dto, CancellationToken.None)
                : await controller.Create(dto, CancellationToken.None);
            Assert.IsType<OkObjectResult>(response.Result);
        }
    }
}
