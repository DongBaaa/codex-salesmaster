using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    [Theory, MemberData(nameof(RentalAssetAmountWriteCases))]
    public async Task RentalHistoryAmountWrite_ExistingMoneyUsesItsOwnHistoryNotCurrentAsset(string permission, int amount)
    {
        var (_, history, log) = await SeedRentalResponsePrivacy(false);
        var user = RentalResponsePrivacyUser(permission); await using var db = CreateDbContext(user);
        var h = history.ToDto(); var l = log.ToDto();
        StampRentalHistoryWrite(h); StampRentalHistoryWrite(l);
        h.MonthlyFee = l.BilledAmount = amount;
        h.ChangeReason = "이력 비고 저장"; l.Note = "청구 비고 저장";
        var result = await PushRentalHistoryWrite(db, user, [h], [l]);
        Assert.Empty(result.Conflicts); Assert.Equal(2, result.AcceptedCount);
        db.ChangeTracker.Clear();
        var savedH = await db.RentalAssetAssignmentHistories.SingleAsync(x => x.Id == h.Id);
        var savedL = await db.RentalBillingLogs.SingleAsync(x => x.Id == l.Id);
        var sales = permission is "sales" or "both" or "admin" or "god";
        Assert.Equal(sales ? amount : history.MonthlyFee, savedH.MonthlyFee);
        Assert.Equal(sales ? amount : log.BilledAmount, savedL.BilledAmount);
        Assert.Equal(h.ChangeReason, savedH.ChangeReason); Assert.Equal(l.Note, savedL.Note);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("purchase")]
    public async Task RentalHistoryAmountWrite_RedactedNotesReplayPreservesConfirmedZero(string permission)
    {
        var (_, history, log) = await SeedRentalResponsePrivacy(false);
        var h0 = await _dbContext.RentalAssetAssignmentHistories.SingleAsync(x => x.Id == history.Id);
        var l0 = await _dbContext.RentalBillingLogs.SingleAsync(x => x.Id == log.Id);
        h0.MonthlyFee = l0.BilledAmount = 0; await _dbContext.SaveChangesAsync();
        var h = h0.ToDto(); var l = l0.ToDto(); StampRentalHistoryWrite(h); StampRentalHistoryWrite(l);
        h.MonthlyFee = l.BilledAmount = null; h.ChangeReason = l.Note = "0원 비고";
        l.Status = "완료"; l.ProcessedDate = new(2099,1,1); l.ProcessedByUsername = "forged";
        var request = JsonSerializer.Serialize(new SyncPushRequest { DeviceId = "history-write", RentalAssetAssignmentHistories = [h], RentalBillingLogs = [l] });
        var user = RentalResponsePrivacyUser(permission); await using var db = CreateDbContext(user);
        long revision = 0;
        for (var retry = 0; retry < 2; retry++)
        {
            var response = await CreateController(db, user).Push(JsonSerializer.Deserialize<SyncPushRequest>(request)!, CancellationToken.None);
            Assert.Empty(Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value).Conflicts);
            db.ChangeTracker.Clear();
            var saved = await db.RentalBillingLogs.SingleAsync(x => x.Id == log.Id);
            Assert.Equal(0m, saved.BilledAmount); Assert.Equal("0원 비고", saved.Note);
            Assert.Equal(log.Status, saved.Status); Assert.Equal(log.ProcessedDate, saved.ProcessedDate);
            Assert.Equal(log.ProcessedByUsername, saved.ProcessedByUsername);
            Assert.Equal(0m, (await db.RentalAssetAssignmentHistories.SingleAsync(x => x.Id == h.Id)).MonthlyFee);
            if (retry == 0) revision = saved.Revision; else Assert.Equal(revision, saved.Revision);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RentalHistoryAmountWrite_NewCurrentAssignmentUsesValidatedServerAsset(bool current)
    {
        var (asset, history, _) = await SeedRentalResponsePrivacy(false);
        var h = history.ToDto(); h.Id = Guid.NewGuid(); h.Revision = h.ExpectedRevision = 0;
        h.IsCurrent = current; h.MonthlyFee = 999; StampRentalHistoryWrite(h);
        var user = RentalResponsePrivacyUser("none"); await using var db = CreateDbContext(user);
        var result = await PushRentalHistoryWrite(db, user, [h], []);
        if (!current)
        {
            Assert.Single(result.Conflicts); Assert.Equal(0, result.AcceptedCount);
            Assert.False(await db.RentalAssetAssignmentHistories.AnyAsync(x => x.Id == h.Id)); return;
        }
        Assert.Empty(result.Conflicts); Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(asset.MonthlyFee, (await db.RentalAssetAssignmentHistories.SingleAsync(x => x.Id == h.Id)).MonthlyFee);
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("zero")]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("invalid")]
    [InlineData("tombstone")]
    public async Task RentalHistoryAmountWrite_NewLogRequiresOneAuthoritativeBillingRun(string source)
    {
        var (_, _, log) = await SeedRentalResponsePrivacy(false);
        var profile = await _dbContext.RentalBillingProfiles.SingleAsync(x => x.Id == log.BillingProfileId);
        var rows = JsonNode.Parse(profile.BillingRunsJson)!.AsArray();
        if (source == "zero") rows[0]!["BilledAmount"] = 0m;
        if (source == "missing") rows.Clear();
        if (source == "ambiguous")
        {
            var copy = rows[0]!.DeepClone(); copy["RunId"] = Guid.NewGuid(); copy["RunKey"] = "another"; rows.Add(copy);
        }
        if (source == "tombstone")
        {
            rows[0]!["IsTombstoned"] = true; rows[0]!["TombstonedAtUtc"] = DateTime.UtcNow; rows[0]!["TombstonedByUsername"] = "admin";
        }
        profile.BillingRunsJson = source == "invalid" ? "broken" : rows.ToJsonString(); await _dbContext.SaveChangesAsync();
        var l = log.ToDto(); l.Id = Guid.NewGuid(); l.Revision = l.ExpectedRevision = 0; StampRentalHistoryWrite(l);
        l.BillingYearMonth = "2026-08"; l.ScheduledDate = new(2026,8,25); l.BilledAmount = 999;
        l.Status = "완료"; l.ProcessedByUsername = "forged";
        var user = RentalResponsePrivacyUser("none"); await using var db = CreateDbContext(user);
        var result = await PushRentalHistoryWrite(db, user, [], [l]);
        if (source is not ("valid" or "zero"))
        {
            Assert.Single(result.Conflicts); Assert.Equal(0, result.AcceptedCount);
            Assert.False(await db.RentalBillingLogs.AnyAsync(x => x.Id == l.Id)); return;
        }
        Assert.Empty(result.Conflicts); var saved = await db.RentalBillingLogs.SingleAsync(x => x.Id == l.Id);
        Assert.Equal(source == "zero" ? 0m : 100000m, saved.BilledAmount);
        Assert.Equal("미입금", saved.Status); Assert.Equal(string.Empty, saved.ProcessedByUsername);
    }

    [Theory]
    [InlineData("asset")]
    [InlineData("profile")]
    [InlineData("month")]
    public async Task RentalHistoryAmountWrite_CannotMoveHiddenHistoricalMoney(string changed)
    {
        var (_, history, log) = await SeedRentalResponsePrivacy(false);
        var (otherAsset, _, _) = await SeedRentalResponsePrivacy(false);
        var h = history.ToDto(); var l = log.ToDto(); StampRentalHistoryWrite(h); StampRentalHistoryWrite(l);
        if (changed == "asset") h.AssetId = otherAsset.Id;
        if (changed == "profile") l.BillingProfileId = otherAsset.BillingProfileId!.Value;
        if (changed == "month") { l.BillingYearMonth = "2026-10"; l.ScheduledDate = new(2026,10,25); }
        var user = RentalResponsePrivacyUser("none"); await using var db = CreateDbContext(user);
        var result = await PushRentalHistoryWrite(db, user, changed == "asset" ? [h] : [], changed == "asset" ? [] : [l]);
        Assert.Single(result.Conflicts); Assert.Equal(0, result.AcceptedCount);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("sales")]
    public async Task RentalHistoryAmountWrite_MemoCannotReplaceMissingHistoricalDatesWithCurrentContract(string permission)
    {
        var (asset, history, _) = await SeedRentalResponsePrivacy(false);
        var current = await _dbContext.RentalAssets.SingleAsync(x => x.Id == asset.Id);
        current.ContractStartDate = new(2026, 9, 1); current.RentalEndDate = new(2029, 8, 31);
        await _dbContext.SaveChangesAsync();
        var user = RentalResponsePrivacyUser(permission); await using var db = CreateDbContext(user);
        var h = history.ToDto(); StampRentalHistoryWrite(h); h.ChangeReason = "과거 날짜 미상 보존";
        var result = await PushRentalHistoryWrite(db, user, [h], []); Assert.Empty(result.Conflicts);
        db.ChangeTracker.Clear(); var saved = await db.RentalAssetAssignmentHistories.SingleAsync(x => x.Id == h.Id);
        Assert.Null(saved.ContractStartDate); Assert.Null(saved.ContractEndDate);
    }

    [Theory]
    [InlineData("sales")]
    [InlineData("both")]
    [InlineData("admin")]
    [InlineData("god")]
    public async Task RentalHistoryAmountWrite_NewlyAuthorizedRedactedPayloadMustReload(string permission)
    {
        var (_, history, log) = await SeedRentalResponsePrivacy(false);
        var user = RentalResponsePrivacyUser(permission); await using var db = CreateDbContext(user);
        var h = history.ToDto(); var l = log.ToDto(); StampRentalHistoryWrite(h); StampRentalHistoryWrite(l);
        h.MonthlyFee = l.BilledAmount = null;
        var result = await PushRentalHistoryWrite(db, user, [h], [l]);
        Assert.Equal(2, result.ConflictCount); Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(history.Revision, (await db.RentalAssetAssignmentHistories.AsNoTracking().SingleAsync(x => x.Id == h.Id)).Revision);
        Assert.Equal(log.Revision, (await db.RentalBillingLogs.AsNoTracking().SingleAsync(x => x.Id == l.Id)).Revision);
    }

    [Fact]
    public async Task RentalHistoryAmountWrite_MissingRowDeletesStillAcknowledgeWithoutCreatingRows()
    {
        var (_, history, log) = await SeedRentalResponsePrivacy(false);
        var h = history.ToDto(); var l = log.ToDto(); h.Id = Guid.NewGuid(); l.Id = Guid.NewGuid();
        h.IsDeleted = l.IsDeleted = true; h.MonthlyFee = l.BilledAmount = null;
        StampRentalHistoryWrite(h); StampRentalHistoryWrite(l);
        var user = RentalResponsePrivacyUser("none"); await using var db = CreateDbContext(user);
        var result = await PushRentalHistoryWrite(db, user, [h], [l]);
        Assert.Empty(result.Conflicts); Assert.Equal(2, result.AcceptedCount);
        Assert.False(await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().AnyAsync(x => x.Id == h.Id));
        Assert.False(await db.RentalBillingLogs.IgnoreQueryFilters().AnyAsync(x => x.Id == l.Id));
    }

    private static void StampRentalHistoryWrite(SyncEntityDto dto)
    {
        dto.ExpectedRevision = dto.Revision; dto.MutationId = Guid.NewGuid().ToString("N");
        dto.MutationCreatedAtUtc = DateTime.UtcNow; dto.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1);
    }

    private async Task<SyncPushResult> PushRentalHistoryWrite(거래플랜.Server.Api.Data.AppDbContext db, TestCurrentUserContext user,
        List<RentalAssetAssignmentHistoryDto> histories, List<RentalBillingLogDto> logs)
    {
        var response = await CreateController(db, user).Push(new SyncPushRequest { DeviceId = "history-write",
            RentalAssetAssignmentHistories = histories, RentalBillingLogs = logs }, CancellationToken.None);
        return Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
    }
}
