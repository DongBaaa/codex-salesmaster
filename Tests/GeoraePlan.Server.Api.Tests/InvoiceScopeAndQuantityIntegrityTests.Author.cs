using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class InvoiceScopeAndQuantityIntegrityTests
{
    [Theory]
    [InlineData(false, "USENET")]
    [InlineData(true, "USENET")]
    [InlineData(false, "YEONSU")]
    [InlineData(true, "YEONSU")]
    [InlineData(false, "ITWORLD")]
    [InlineData(true, "ITWORLD")]
    public async Task InvoiceAuthor_AcceptedWritesUseAuthenticatedAccount_AndReplayOrConflictCannotChangeIt(bool sync, string office)
    {
        TestCurrentUserContext User(string name) => new()
        {
            Username = name, OfficeCode = office,
            TenantCode = office == "ITWORLD" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly, Permissions = [PermissionNames.InvoiceEdit]
        };
        var alice = User("author-alice");
        await using var db = CreateDbContext(alice);
        var customer = CreateCustomer(office);
        var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        item.OfficeCode = office; item.TenantCode = alice.TenantCode;
        db.Customers.Add(customer); db.Items.Add(item); await db.SaveChangesAsync();
        var dto = BuildInvoiceDto(Guid.NewGuid(), customer, item, office + "_MAIN", 2m, ItemTrackingTypes.NonStock);
        dto.Author = new InvoiceAuthorDto { CreatedByUsername = "forged-creator", LastSavedByUsername = "forged-editor", LastSavedAtUtc = DateTime.UnixEpoch };
        dto.MutationId = "author-create-" + Guid.NewGuid().ToString("N");
        var original = JsonSerializer.Serialize(dto);
        var before = DateTime.UtcNow;
        await Save(dto, alice, create: true, accepted: true);
        db.ChangeTracker.Clear();
        var created = await Load();
        Assert.Equal(alice.Username, created.CreatedByUsername);
        Assert.Equal(alice.Username, created.LastSavedByUsername);
        Assert.InRange(created.LastSavedAtUtc!.Value, before, DateTime.UtcNow);
        Assert.Equal(200m, created.TotalAmount); // Staff has no amount permission; server price remains authoritative.
        var createdRevision = created.Revision; var createdAt = created.LastSavedAtUtc;
        await Save(JsonSerializer.Deserialize<InvoiceDto>(original)!, alice, create: true, accepted: true);
        db.ChangeTracker.Clear(); created = await Load();
        Assert.Equal(createdRevision, created.Revision); Assert.Equal(createdAt, created.LastSavedAtUtc);

        var edit = created.ToDto(); edit.ExpectedRevision = created.Revision;
        edit.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1); edit.MutationId = "author-edit-" + Guid.NewGuid().ToString("N");
        edit.Memo = "allowed staff memo";
        edit.Author = new InvoiceAuthorDto { CreatedByUsername = "spoofed", LastSavedByUsername = "spoofed" };
        await Save(edit, User("author-bob"), create: false, accepted: true);
        db.ChangeTracker.Clear(); var edited = await Load();
        Assert.Equal(alice.Username, edited.CreatedByUsername); Assert.Equal("author-bob", edited.LastSavedByUsername);
        Assert.Equal("allowed staff memo", edited.Memo); Assert.Equal(200m, edited.TotalAmount);
        var editedAt = edited.LastSavedAtUtc; var editedRevision = edited.Revision;
        var stale = edited.ToDto(); stale.ExpectedRevision = createdRevision; stale.Revision = createdRevision;
        stale.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1); stale.MutationId = "author-stale-" + Guid.NewGuid().ToString("N"); stale.Memo = "must not save";
        await Save(stale, alice, create: false, accepted: false);
        db.ChangeTracker.Clear(); var stored = await Load();
        Assert.Equal(editedRevision, stored.Revision); Assert.Equal(editedAt, stored.LastSavedAtUtc);
        Assert.Equal("author-bob", stored.LastSavedByUsername);
        var response = Assert.IsType<OkObjectResult>((await CreateInvoicesController(db, alice).GetById(dto.Id, default)).Result);
        Assert.Equal("author-bob", Assert.IsType<InvoiceDto>(response.Value).Author!.LastSavedByUsername);

        Task<Invoice> Load() => db.Invoices.Include(x => x.Lines).SingleAsync(x => x.Id == dto.Id);
        async Task Save(InvoiceDto value, TestCurrentUserContext user, bool create, bool accepted)
        {
            // Use a context bound to the authenticated actor for audit identity as well.
            await using var requestDb = CreateDbContext(user);
            if (sync)
            {
                var result = await CreateSyncController(requestDb, user).Push(new SyncPushRequest { DeviceId = "author-test", Invoices = [value] }, default);
                var pushed = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
                Assert.Equal(accepted, pushed.Conflicts.Count == 0);
                if (accepted) Assert.True(pushed.AcceptedCount > 0 || pushed.DuplicateMutationCount > 0);
            }
            else
            {
                var controller = CreateInvoicesController(requestDb, user);
                var result = create ? await controller.Create(value, default) : await controller.Update(value.Id, value, default);
                if (accepted) Assert.IsType<OkObjectResult>(result.Result);
                else Assert.IsType<ConflictObjectResult>(result.Result);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvoiceAuthor_NewVersionPreservesKnownOrUnknownOriginalCreator(bool unknownCreator)
    {
        var user = CreateInvoiceUser("new-version-editor");
        await using var db = CreateDbContext(user);
        var customer = CreateCustomer("USENET"); var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var previous = CreateInvoice(Guid.NewGuid(), customer, item, 1m, ItemTrackingTypes.NonStock);
        previous.CreatedByUsername = unknownCreator ? "" : "original-author";
        previous.LastSavedByUsername = "previous-editor"; previous.LastSavedAtUtc = DateTime.UnixEpoch;
        db.Customers.Add(customer); db.Items.Add(item); db.Invoices.Add(previous); await db.SaveChangesAsync();
        var dto = previous.ToDto(); dto.Id = Guid.NewGuid(); dto.VersionGroupId = previous.Id; dto.VersionNumber = 2;
        dto.PreviousVersionId = previous.Id; dto.ExpectedRevision = previous.Revision; dto.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
        dto.MutationId = "author-version-" + Guid.NewGuid().ToString("N"); dto.Author!.CreatedByUsername = "forged";
        foreach (var line in dto.Lines) { line.Id = Guid.NewGuid(); line.InvoiceId = dto.Id; }
        var result = await CreateSyncController(db, user).Push(new SyncPushRequest { DeviceId = "author-version", Invoices = [dto] }, default);
        var pushed = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Empty(pushed.Conflicts);
        db.ChangeTracker.Clear();
        var latest = await db.Invoices.SingleAsync(x => x.Id == dto.Id);
        Assert.Equal(unknownCreator ? "" : "original-author", latest.CreatedByUsername);
        Assert.Equal(user.Username, latest.LastSavedByUsername);
        var old = await db.Invoices.SingleAsync(x => x.Id == previous.Id);
        Assert.Equal("previous-editor", old.LastSavedByUsername); Assert.Equal(DateTime.UnixEpoch, old.LastSavedAtUtc);
    }

    [Fact]
    public async Task InvoiceAuthor_LegacySchemaUpgradePreservesRowsAndUnknownIdentity()
    {
        await using var db = CreateDbContext(CreateAdminUser());
        var customer = CreateCustomer("USENET"); var item = CreateItem(ItemTrackingTypes.NonStock, 0m);
        var invoice = CreateInvoice(Guid.NewGuid(), customer, item, 1m, ItemTrackingTypes.NonStock);
        db.Customers.Add(customer); db.Items.Add(item); db.Invoices.Add(invoice); await db.SaveChangesAsync();
        var revision = invoice.Revision; db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE Invoices DROP COLUMN CreatedByUsername; ALTER TABLE Invoices DROP COLUMN LastSavedByUsername; ALTER TABLE Invoices DROP COLUMN LastSavedAtUtc;");
        var upgrade = typeof(DbInitializer).GetMethod("EnsureInvoiceAuthorColumnsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        for (var i = 0; i < 2; i++) await (Task)upgrade.Invoke(null, [db, CancellationToken.None])!;
        var stored = await db.Invoices.SingleAsync(x => x.Id == invoice.Id);
        Assert.Equal(revision, stored.Revision); Assert.Equal(invoice.Memo, stored.Memo); Assert.Equal(invoice.TotalAmount, stored.TotalAmount);
        Assert.Equal("", stored.CreatedByUsername); Assert.Equal("", stored.LastSavedByUsername); Assert.Null(stored.LastSavedAtUtc);
    }

    [Fact]
    public void InvoiceAuthor_ResponseMetadataDoesNotChangeCurrentOrLegacyMutationHash()
    {
        var dto = new InvoiceDto { Id = Guid.NewGuid(), MutationId = "  historical:AbC  ", Memo = "preserve business payload" };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Assert.DoesNotContain("author", JsonSerializer.Serialize(dto, options));
        var raw = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(dto, options))).ToLowerInvariant();
        var canonical = SyncMutationPayloadHasher.Compute(dto);
        dto.Author = new InvoiceAuthorDto { CreatedByUsername = "server-author", LastSavedByUsername = "server-editor", LastSavedAtUtc = DateTime.UtcNow };
        Assert.Equal(canonical, SyncMutationPayloadHasher.Compute(dto));
        Assert.True(SyncMutationPayloadHasher.Matches(dto, raw, dto.MutationId));
        dto.Memo += " changed";
        Assert.False(SyncMutationPayloadHasher.Matches(dto, raw, dto.MutationId));
    }
}
