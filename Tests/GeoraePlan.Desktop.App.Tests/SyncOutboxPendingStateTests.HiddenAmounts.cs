using System.Text.Json;
using System.Text.Json.Serialization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed partial class SyncOutboxPendingStateTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task HiddenInvoiceAmounts_JsonAndDatabaseRoundtripNeverPublishesZero(bool web, bool hidden)
    {
        PrepareAppRoot("georaeplan-hidden-invoice-amounts");
        try
        {
            var options = new JsonSerializerOptions(web ? JsonSerializerDefaults.Web : JsonSerializerDefaults.General)
            { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
            var invoice = new InvoiceDto
            {
                Id = Guid.NewGuid(), CustomerId = Guid.NewGuid(), Memo = "수량·비고 보존",
                TotalAmount = hidden ? null : 0m, SupplyAmount = hidden ? null : 0m, VatAmount = hidden ? null : 0m,
                Lines = [new InvoiceLineDto
                {
                    Id = Guid.NewGuid(), ItemNameOriginal = "품목", Quantity = 3,
                    UnitPrice = hidden ? null : 0m, LineAmount = hidden ? null : 0m, Remark = "수량 3"
                }]
            };
            invoice.Lines[0].InvoiceId = invoice.Id;
            var json = JsonSerializer.Serialize(invoice, options);
            using var document = JsonDocument.Parse(json);
            var total = document.RootElement.GetProperty(web ? "totalAmount" : "TotalAmount");
            Assert.Equal(hidden ? JsonValueKind.Null : JsonValueKind.Number, total.ValueKind);
            if (hidden)
                Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<LegacyVisibleInvoice>(json, options));
            else
                Assert.Equal(0m, JsonSerializer.Deserialize<LegacyVisibleInvoice>(json, options)!.TotalAmount);
            var decoded = JsonSerializer.Deserialize<InvoiceDto>(json, options)!;
            Assert.Equal(hidden, decoded.AmountsHidden);
            await using (var db = new LocalDbContext())
            {
                await db.Database.EnsureDeletedAsync();
                await db.Database.EnsureCreatedAsync();
                var local = LocalMappings.ToLocal(decoded);
                db.Invoices.Add(local);
                await db.SaveChangesAsync();
            }
            await using (var reloadedDb = new LocalDbContext())
            {
                var local = await reloadedDb.Invoices.Include(x => x.Lines).SingleAsync();
                Assert.Equal(hidden, local.AmountsHidden);
                Assert.Equal(hidden, Assert.Single(local.Lines).AmountsHidden);
                local.Memo = "수정한 비고";
                local.Lines.Single().Quantity = 4;
                local.IsDirty = true;
                await reloadedDb.SaveChangesAsync();
                var outgoing = LocalMappings.ToDto(local);
                Assert.Equal(hidden, outgoing.AmountsHidden);
                Assert.Equal(hidden ? null : 0m, outgoing.TotalAmount);
                Assert.Equal(hidden ? null : 0m, outgoing.Lines[0].UnitPrice);
                Assert.Equal(hidden ? null : 0m, outgoing.Lines[0].LineAmount);
                Assert.Equal(4, outgoing.Lines[0].Quantity);
                Assert.Equal("수정한 비고", outgoing.Memo);
                using var sent = JsonDocument.Parse(JsonSerializer.Serialize(outgoing, options));
                Assert.Equal(hidden ? JsonValueKind.Null : JsonValueKind.Number,
                    sent.RootElement.GetProperty(web ? "totalAmount" : "TotalAmount").ValueKind);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void HiddenInvoiceAmounts_PartialMoneyCannotBecomeAnApparentVisibleZero()
    {
        var invoice = new InvoiceDto
        {
            TotalAmount = 3300, SupplyAmount = 3000, VatAmount = 300,
            Lines = [new InvoiceLineDto { Quantity = 3, UnitPrice = null, LineAmount = 3300 }]
        };
        Assert.True(invoice.AmountsHidden);
        var local = LocalMappings.ToLocal(invoice);
        Assert.True(local.AmountsHidden);
        var outgoing = LocalMappings.ToDto(local);
        Assert.Null(outgoing.TotalAmount);
        Assert.Null(outgoing.Lines[0].UnitPrice);
        Assert.Null(outgoing.Lines[0].LineAmount);
        Assert.Throws<InvalidOperationException>(() => DisclosedAmount.Require(outgoing.TotalAmount));
    }

    private sealed class LegacyVisibleInvoice
    {
        public decimal TotalAmount { get; set; }
    }

    [Fact]
    public void HiddenInvoiceAmounts_MobileDraftPreservesNullThroughQuantityEdit()
    {
        var line = GeoraePlan.Mobile.App.Models.InvoiceLineDraftItem.FromDto(new InvoiceLineDto
        { Id = Guid.NewGuid(), Quantity = 2, UnitPrice = null, LineAmount = null, Remark = "기존 비고" });
        line.Quantity = 5;
        line.Remark = "수정 비고";
        var sent = line.ToDto(Guid.NewGuid());
        Assert.Null(sent.UnitPrice);
        Assert.Null(sent.LineAmount);
        Assert.Equal(5, sent.Quantity);
        Assert.Equal("수정 비고", sent.Remark);
    }

    [Fact]
    public async Task HiddenInvoiceAmounts_LegacyColumnUpgradePreservesExistingPricesAndDirtyState()
    {
        PrepareAppRoot("georaeplan-hidden-amount-schema");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            db.Invoices.Add(new LocalInvoice
            {
                Id = Guid.NewGuid(), TotalAmount = 1234, SupplyAmount = 1122, VatAmount = 112, IsDirty = true,
                Lines = [new LocalInvoiceLine { Quantity = 2, UnitPrice = 617, LineAmount = 1234 }]
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE Invoices DROP COLUMN AmountsHidden;");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE InvoiceLines DROP COLUMN AmountsHidden;");
            var migrate = typeof(LocalDbInitializer).GetMethod("MigrateColumnsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
            await (Task)migrate.Invoke(null, [db])!;
            await (Task)migrate.Invoke(null, [db])!;
            var stored = await db.Invoices.Include(x => x.Lines).SingleAsync();
            Assert.False(stored.AmountsHidden);
            Assert.False(stored.Lines.Single().AmountsHidden);
            Assert.True(stored.IsDirty);
            Assert.Equal(1234, stored.TotalAmount);
            Assert.Equal(617, stored.Lines.Single().UnitPrice);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task HiddenInvoiceAmounts_ActualLocalSaveKeepsUnknownMoney()
    {
        PrepareAppRoot("georaeplan-hidden-amount-save");
        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();
            var customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "시험 거래처",
                TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "hidden-editor",
                Role = DomainConstants.RoleUser, TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                Permissions = [AppPermissionNames.InvoiceEdit] });
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var draft = LocalMappings.ToLocal(new InvoiceDto { Id = Guid.NewGuid(), CustomerId = customer.Id,
                OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TotalAmount = null, SupplyAmount = null, VatAmount = null, Memo = "비금액 저장",
                Lines = [new InvoiceLineDto { Id = Guid.NewGuid(), ItemNameOriginal = "시험 품목", Quantity = 3,
                    UnitPrice = null, LineAmount = null, ItemTrackingType = ItemTrackingTypes.NonStock }] });
            var result = await local.SaveInvoiceAsync(draft, new InvoiceSaveContext
            { Username = "hidden-editor", Role = DomainConstants.RoleUser, OfficeCode = OfficeCodeCatalog.Usenet }, session);
            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            var saved = await db.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == result.SavedInvoiceId);
            Assert.True(saved.AmountsHidden);
            Assert.True(saved.IsDirty);
            Assert.Equal("비금액 저장", saved.Memo);
            var outgoing = LocalMappings.ToDto(saved);
            Assert.Null(outgoing.TotalAmount);
            Assert.Null(outgoing.Lines.Single().UnitPrice);
            Assert.Equal(3, outgoing.Lines.Single().Quantity);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }
}
