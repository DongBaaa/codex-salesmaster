using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Middleware;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    public static IEnumerable<object[]> RentalResponsePrivacyCases()
    {
        foreach (var permission in new[] { "none", "purchase", "sales", "both", "admin", "god" })
        foreach (var deleted in new[] { false, true })
        foreach (var administrationOnly in new[] { false, true })
            yield return [permission, deleted, administrationOnly];
    }

    [Theory, MemberData(nameof(RentalResponsePrivacyCases))]
    public async Task RentalResponsePrivacy_PullMasksEachDirectionIncludingTombstones(string permission, bool deleted, bool administrationOnly)
    {
        var (asset, history, log) = await SeedRentalResponsePrivacy(deleted, deleteProfile: false);
        var before = JsonSerializer.Serialize(new { Asset = asset.ToDto(), History = history.ToDto(), Log = log.ToDto() });
        var user = RentalResponsePrivacyUser(permission);
        await using var db = CreateDbContext(user);
        var result = await CreateController(db, user).Pull(0, CancellationToken.None, rentalAdministrationOnly: administrationOnly);
        var pull = Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var a = Assert.Single(pull.RentalAssets, x => x.Id == asset.Id);
        var h = Assert.Single(pull.RentalAssetAssignmentHistories, x => x.Id == history.Id);
        var l = Assert.Single(pull.RentalBillingLogs, x => x.Id == log.Id);
        var sales = permission is "sales" or "both" or "admin" or "god";
        var purchase = permission is "purchase" or "both" or "admin" or "god";
        Assert.Equal(purchase ? 123456m : null, a.PurchasePrice);
        Assert.Equal(sales ? 765432m : null, a.SalePrice);
        Assert.Equal(sales ? 654321m : null, a.MonthlyFee);
        Assert.Equal(sales ? "deposit amount" : null, a.DepositText);
        Assert.Equal(sales ? 12.5m : null, a.BlackOverageUnitPrice);
        Assert.Equal(sales ? 45.6m : null, a.ColorOverageUnitPrice);
        Assert.Equal(sales ? 234567m : null, h.MonthlyFee);
        Assert.Equal(sales ? 345678m : null, l.BilledAmount);
        Assert.Equal(!purchase, a.PurchaseAmountsHidden); Assert.Equal(!sales, a.SalesAmountsHidden);
        Assert.Equal("keep asset note", a.Notes); Assert.Equal("keep history reason", h.ChangeReason); Assert.Equal("keep log note", l.Note);
        Assert.Equal(asset.MeterReadingsJson, a.MeterReadingsJson); Assert.Equal(500, a.BlackIncludedPages);
        Assert.Equal(asset.Revision, a.Revision); Assert.Equal(deleted, a.IsDeleted); Assert.Equal(asset.Id, h.AssetId);
        db.ChangeTracker.Clear();
        Assert.Equal(before, JsonSerializer.Serialize(new {
            Asset = (await db.RentalAssets.IgnoreQueryFilters().SingleAsync(x => x.Id == asset.Id)).ToDto(),
            History = (await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().SingleAsync(x => x.Id == history.Id)).ToDto(),
            Log = (await db.RentalBillingLogs.IgnoreQueryFilters().SingleAsync(x => x.Id == log.Id)).ToDto() }));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("purchase")]
    [InlineData("sales")]
    [InlineData("both")]
    public async Task RentalResponsePrivacy_ConflictCopiesHideMoneyAndPreserveStoredAudit(string permission)
    {
        var (asset, history, log) = await SeedRentalResponsePrivacy(false);
        var user = RentalResponsePrivacyUser(permission);
        await using var db = CreateDbContext(user);
        var a = asset.ToDto(); var h = history.ToDto(); var l = log.ToDto();
        foreach (var dto in new SyncEntityDto[] { a, h, l })
        {
            dto.ExpectedRevision = dto.Revision + 1;
            dto.MutationId = Guid.NewGuid().ToString("N"); dto.UpdatedAtUtc = DateTime.UtcNow.AddMinutes(1);
        }
        var response = await CreateController(db, user).Push(new SyncPushRequest {
            DeviceId = "rental-response-privacy", RentalAssets = [a], RentalAssetAssignmentHistories = [h], RentalBillingLogs = [l] }, CancellationToken.None);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.Equal(0, result.AcceptedCount);
        foreach (var (entity, field, amount) in new[] { ("RentalAsset", "MonthlyFee", 654321m), ("RentalAssetAssignmentHistory", "MonthlyFee", 234567m), ("RentalBillingLog", "BilledAmount", 345678m) })
        {
            var conflict = Assert.Single(result.Conflicts, c => c.EntityName == entity);
            foreach (var raw in new[] { conflict.ClientJson, conflict.ServerJson })
            {
                Assert.False(string.IsNullOrWhiteSpace(raw));
                var snapshot = JsonNode.Parse(raw)!;
                Assert.Equal(permission is "sales" or "both" ? amount : null, snapshot[field]?.GetValue<decimal>());
                if (entity == "RentalAsset")
                    Assert.Equal(permission is "purchase" or "both" ? 123456m : null, snapshot["PurchasePrice"]?.GetValue<decimal>());
            }
            var stored = await db.ConflictLogs.AsNoTracking().SingleAsync(c => c.Id == conflict.Id);
            Assert.Equal(amount, JsonNode.Parse(stored.ServerJson)![field]!.GetValue<decimal>());
        }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("purchase")]
    [InlineData("sales")]
    [InlineData("admin")]
    public async Task RentalResponsePrivacy_RecycleBinDoesNotDiscloseMoneyInText(string permission)
    {
        var (asset, _, log) = await SeedRentalResponsePrivacy(true);
        var user = RentalResponsePrivacyUser(permission);
        await using var db = CreateDbContext(user);
        var controller = new RecycleBinController(db, new OfficeScopeService(user, db), null!, null!, null!, null!, null!);
        var result = await controller.GetAll(null, null, CancellationToken.None);
        var entries = Assert.IsType<List<RecycleBinEntryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var sales = permission is "sales" or "admin";
        var detail = Assert.Single(entries, e => e.EntityId == asset.Id).Detail;
        Assert.Equal(sales, detail.Contains("654,321"));
        detail = Assert.Single(entries, e => e.EntityId == log.Id).Detail;
        Assert.Equal(sales, detail.Contains("345,678")); Assert.Contains("keep log note", detail);
        detail = Assert.Single(entries, e => e.EntityId == asset.BillingProfileId).Detail;
        Assert.Equal(sales, detail.Contains("100,000"));
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public async Task RentalResponsePrivacy_PurchaseHiddenAlsoRequiresRentalProtocol(int protocol, bool allowed)
    {
        var user = RentalResponsePrivacyUser("sales");
        await using var db = CreateDbContext(user);
        var context = new DefaultHttpContext(); context.Request.Path = "/sync/pull";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "reader")], "test"));
        context.Response.Body = new MemoryStream();
        context.Request.Headers[ClientCompatibilityHeaders.AppId] = "georaeplan-desktop";
        context.Request.Headers[ClientCompatibilityHeaders.Platform] = "windows";
        context.Request.Headers[ClientCompatibilityHeaders.Version] = "1.1.743";
        context.Request.Headers[ClientCompatibilityHeaders.Build] = "743";
        context.Request.Headers[ClientCompatibilityHeaders.Protocol] = protocol.ToString();
        var called = false;
        await new InvoiceAmountCompatibilityMiddleware(_ => { called = true; return Task.CompletedTask; })
            .InvokeAsync(context, new OfficeScopeService(user, db));
        Assert.Equal(allowed, called);
        if (!allowed) Assert.Equal(426, context.Response.StatusCode);
    }

    private static TestCurrentUserContext RentalResponsePrivacyUser(string permission) => new() {
        Username = "rental-response-reader", IsAdmin = permission == "admin", IsGodMode = permission == "god",
        Permissions = new[] { PermissionNames.RentalViewAll, PermissionNames.RentalProfileEdit, PermissionNames.RentalAssetEdit, PermissionNames.DataBackupRestore }
            .Concat(permission is "sales" or "both" ? [PermissionNames.AmountViewSales] : Array.Empty<string>())
            .Concat(permission is "purchase" or "both" ? [PermissionNames.AmountViewPurchase] : Array.Empty<string>()).ToArray() };

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"MonthlyFee\":1,\"MonthlyFee\":2}")]
    public void RentalResponsePrivacy_MalformedConflictFailsClosed(string raw)
    {
        var user = RentalResponsePrivacyUser("none");
        using var db = CreateDbContext(user);
        var conflict = new ConflictLogDto { EntityName = "RentalAsset", ClientJson = raw, ServerJson = raw };
        RentalAssetAmountReadPolicy.ApplyConflicts([conflict], new OfficeScopeService(user, db));
        Assert.Equal(string.Empty, conflict.ClientJson); Assert.Equal(string.Empty, conflict.ServerJson);
    }

    [Fact]
    public void RentalResponsePrivacy_CaseAliasesCannotLeakAndUnrelatedConflictsRemainIntact()
    {
        const string raw = "{\"purchasePrice\":123,\"PurchasePrice\":456,\"monthlyfee\":789,\"MonthlyFee\":987,\"salesAmountsHidden\":false,\"Notes\":\"keep\"}";
        var user = RentalResponsePrivacyUser("none");
        using var db = CreateDbContext(user);
        var asset = new ConflictLogDto { EntityName = "RentalAsset", ClientJson = raw, ServerJson = raw };
        var unrelated = new ConflictLogDto { EntityName = "Customer", ClientJson = raw, ServerJson = raw };
        RentalAssetAmountReadPolicy.ApplyConflicts([asset, unrelated], new OfficeScopeService(user, db));
        var row = JsonNode.Parse(asset.ServerJson)!.AsObject();
        Assert.Null(row["PurchasePrice"]); Assert.Null(row["MonthlyFee"]);
        Assert.False(row.ContainsKey("purchasePrice")); Assert.False(row.ContainsKey("monthlyfee"));
        Assert.True(row["SalesAmountsHidden"]!.GetValue<bool>()); Assert.Equal("keep", row["Notes"]!.GetValue<string>());
        Assert.Equal(asset.ServerJson, asset.ClientJson);
        Assert.Equal(raw, unrelated.ClientJson); Assert.Equal(raw, unrelated.ServerJson);
    }

    private async Task<(RentalAsset Asset, RentalAssetAssignmentHistory History, RentalBillingLog Log)> SeedRentalResponsePrivacy(bool deleted, bool deleteProfile = true)
    {
        var profile = await SeedRentalAmountWriteContractAsync(false);
        var a = new RentalAsset { AssetKey = "READ-" + Guid.NewGuid().ToString("N"), ManagementNumber = "READ-001",
            TenantCode = profile.TenantCode, OfficeCode = profile.OfficeCode, ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
            ManagementCompanyCode = profile.OfficeCode, BillingProfileId = profile.Id, PurchasePrice = 123456m, SalePrice = 765432m,
            MonthlyFee = 654321m, DepositText = "deposit amount", BlackOverageUnitPrice = 12.5m, ColorOverageUnitPrice = 45.6m,
            BlackIncludedPages = 500, MeterReadingsJson = "[{\"BlackMeter\":1234,\"Note\":\"keep meter\"}]", Notes = "keep asset note", IsDeleted = deleted };
        var h = new RentalAssetAssignmentHistory { AssetId = a.Id, BillingProfileId = profile.Id,
            TenantCode = profile.TenantCode, OfficeCode = profile.OfficeCode, ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
            MonthlyFee = 234567m, ChangeReason = "keep history reason", IsDeleted = deleted };
        var l = new RentalBillingLog { BillingProfileId = profile.Id, BillingYearMonth = "2026-09", ScheduledDate = new(2026,9,25),
            TenantCode = profile.TenantCode, OfficeCode = profile.OfficeCode, ResponsibleOfficeCode = profile.ResponsibleOfficeCode,
            BilledAmount = 345678m, Note = "keep log note", IsDeleted = deleted };
        if (deleted && deleteProfile) (await _dbContext.RentalBillingProfiles.SingleAsync(x => x.Id == profile.Id)).IsDeleted = true;
        _dbContext.AddRange(a, h, l); await _dbContext.SaveChangesAsync(); _dbContext.ChangeTracker.Clear();
        // Read the persisted representation before comparing it with a later DB read.
        return (await _dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == a.Id),
            await _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == h.Id),
            await _dbContext.RentalBillingLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == l.Id));
    }
}
