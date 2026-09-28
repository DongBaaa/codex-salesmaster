using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    public static IEnumerable<object[]> RentalAssetAmountWriteCases()
    {
        foreach (var permission in new[] { "none", "purchase", "sales", "both", "admin", "god" })
        foreach (var value in new[] { 0, 999 }) yield return [permission, value];
    }

    [Theory, MemberData(nameof(RentalAssetAmountWriteCases))]
    public async Task RentalAssetAmountWrite_ExistingAssetUsesServerMoneyForHiddenDirection(string permission, int value)
    {
        var (asset, _, _) = await SeedRentalResponsePrivacy(false);
        var user = RentalResponsePrivacyUser(permission);
        await using var db = CreateDbContext(user);
        var dto = RentalAssetWriteCommand(asset);
        dto.PurchasePrice = dto.SalePrice = dto.MonthlyFee = dto.BlackOverageUnitPrice = dto.ColorOverageUnitPrice = value;
        dto.DepositText = value.ToString(); dto.Notes = "금액 없이 비고 저장";
        var result = await PushRentalAssetWrite(db, user, dto);
        Assert.Empty(result.Conflicts); Assert.Equal(1, result.AcceptedCount);
        db.ChangeTracker.Clear();
        var saved = await db.RentalAssets.SingleAsync(x => x.Id == asset.Id);
        var purchase = permission is "purchase" or "both" or "admin" or "god";
        var sales = permission is "sales" or "both" or "admin" or "god";
        Assert.Equal(purchase ? value : asset.PurchasePrice, saved.PurchasePrice);
        Assert.Equal(sales ? value : asset.SalePrice, saved.SalePrice);
        Assert.Equal(sales ? value : asset.MonthlyFee, saved.MonthlyFee);
        Assert.Equal(sales ? value.ToString() : asset.DepositText, saved.DepositText);
        Assert.Equal(sales ? value : asset.BlackOverageUnitPrice, saved.BlackOverageUnitPrice);
        Assert.Equal(sales ? value : asset.ColorOverageUnitPrice, saved.ColorOverageUnitPrice);
        Assert.Equal(dto.Notes, saved.Notes); Assert.Equal(asset.BillingProfileId, saved.BillingProfileId);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("purchase")]
    [InlineData("sales")]
    public async Task RentalAssetAmountWrite_RedactedReadCanSaveMemoAndReplayWithoutRewinding(string permission)
    {
        var (asset, _, _) = await SeedRentalResponsePrivacy(false);
        var user = RentalResponsePrivacyUser(permission);
        await using var db = CreateDbContext(user);
        var dto = RentalAssetWriteCommand(asset);
        if (permission != "purchase") dto.PurchasePrice = null;
        if (permission != "sales")
        {
            dto.SalePrice = dto.MonthlyFee = dto.BlackOverageUnitPrice = dto.ColorOverageUnitPrice = null;
            dto.DepositText = null;
        }
        dto.Notes = "비공개 왕복 비고";
        var raw = JsonSerializer.Serialize(dto);
        var result = await PushRentalAssetWrite(db, user, JsonSerializer.Deserialize<RentalAssetDto>(raw)!);
        Assert.Empty(result.Conflicts); Assert.Equal(1, result.AcceptedCount);
        db.ChangeTracker.Clear();
        var saved = await db.RentalAssets.SingleAsync(x => x.Id == asset.Id);
        Assert.Equal(asset.PurchasePrice, saved.PurchasePrice); Assert.Equal(asset.MonthlyFee, saved.MonthlyFee);
        Assert.Equal(asset.BlackOverageUnitPrice, saved.BlackOverageUnitPrice); Assert.Equal(dto.Notes, saved.Notes);
        var revision = saved.Revision;
        result = await PushRentalAssetWrite(db, user, JsonSerializer.Deserialize<RentalAssetDto>(raw)!);
        Assert.Empty(result.Conflicts);
        db.ChangeTracker.Clear();
        Assert.Equal(revision, (await db.RentalAssets.SingleAsync(x => x.Id == asset.Id)).Revision);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("purchase")]
    [InlineData("sales")]
    public async Task RentalAssetAmountWrite_NewAssetCannotTurnUnknownMoneyIntoZero(string permission)
    {
        var (asset, _, _) = await SeedRentalResponsePrivacy(false);
        var user = RentalResponsePrivacyUser(permission);
        await using var db = CreateDbContext(user);
        var dto = RentalAssetWriteCommand(asset); dto.Id = Guid.NewGuid(); dto.Revision = dto.ExpectedRevision = 0;
        dto.ManagementId = dto.ManagementNumber = "NEW-" + dto.Id.ToString("N"); dto.AssetKey = dto.ManagementNumber;
        var result = await PushRentalAssetWrite(db, user, dto);
        Assert.Single(result.Conflicts); Assert.Equal(0, result.AcceptedCount);
        Assert.False(await db.RentalAssets.IgnoreQueryFilters().AnyAsync(x => x.Id == dto.Id));
    }

    [Theory]
    [InlineData("purchase")]
    [InlineData("sales")]
    [InlineData("both")]
    public async Task RentalAssetAmountWrite_AuthorizedButStaleRedactedPayloadMustReload(string permission)
    {
        var (asset, _, _) = await SeedRentalResponsePrivacy(false);
        var user = RentalResponsePrivacyUser(permission);
        await using var db = CreateDbContext(user);
        var dto = RentalAssetWriteCommand(asset); dto.PurchasePrice = dto.MonthlyFee = null;
        var result = await PushRentalAssetWrite(db, user, dto);
        Assert.Single(result.Conflicts); Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(asset.Revision, (await db.RentalAssets.AsNoTracking().SingleAsync(x => x.Id == asset.Id)).Revision);
    }

    [Fact]
    public async Task RentalAssetAmountWrite_HiddenFeeCannotBeMovedToAnotherBillingProfile()
    {
        var (asset, _, _) = await SeedRentalResponsePrivacy(false);
        var other = await SeedRentalAmountWriteContractAsync(false);
        var user = RentalResponsePrivacyUser("none");
        await using var db = CreateDbContext(user);
        var dto = RentalAssetWriteCommand(asset); dto.BillingProfileId = other.Id;
        var result = await PushRentalAssetWrite(db, user, dto);
        Assert.Single(result.Conflicts); Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(asset.BillingProfileId, (await db.RentalAssets.AsNoTracking().SingleAsync(x => x.Id == asset.Id)).BillingProfileId);
    }

    [Theory]
    [InlineData("months")]
    [InlineData("start")]
    [InlineData("end")]
    [InlineData("date")]
    [InlineData("meter")]
    [InlineData("black-pages")]
    [InlineData("color-pages")]
    [InlineData("eligibility")]
    public async Task RentalAssetAmountWrite_HiddenSalesCannotChangeCalculationBasis(string field)
    {
        var (asset, _, _) = await SeedRentalResponsePrivacy(false);
        var user = RentalResponsePrivacyUser("none");
        await using var db = CreateDbContext(user);
        var dto = RentalAssetWriteCommand(asset);
        switch (field)
        {
            case "months": dto.ContractMonths++; break;
            case "start": dto.ContractStartDate = new(2026, 1, 1); break;
            case "end": dto.RentalEndDate = new(2027, 1, 1); break;
            case "date": dto.ContractDate = new(2026, 1, 1); break;
            case "meter": dto.MeterBillingEnabled = !dto.MeterBillingEnabled; break;
            case "black-pages": dto.BlackIncludedPages = 10000; break;
            case "color-pages": dto.ColorIncludedPages = 10000; break;
            case "eligibility": dto.BillingEligibilityStatus = "changed"; break;
        }
        var result = await PushRentalAssetWrite(db, user, dto);
        Assert.Single(result.Conflicts); Assert.Equal(0, result.AcceptedCount);
        Assert.Equal(asset.Revision, (await db.RentalAssets.AsNoTracking().SingleAsync(x => x.Id == asset.Id)).Revision);
    }

    [Fact]
    public async Task RentalAssetAmountWrite_ConfirmedZeroAndMissingOverageRemainDistinct()
    {
        var (asset, _, _) = await SeedRentalResponsePrivacy(false);
        var tracked = await _dbContext.RentalAssets.SingleAsync(x => x.Id == asset.Id);
        tracked.PurchasePrice = tracked.MonthlyFee = tracked.SalePrice = 0;
        tracked.BlackOverageUnitPrice = null; tracked.ColorOverageUnitPrice = 0;
        await _dbContext.SaveChangesAsync(); asset = tracked;
        var user = RentalResponsePrivacyUser("none");
        await using var db = CreateDbContext(user);
        var dto = RentalAssetWriteCommand(asset);
        dto.PurchasePrice = dto.MonthlyFee = dto.SalePrice = dto.BlackOverageUnitPrice = dto.ColorOverageUnitPrice = 999;
        var result = await PushRentalAssetWrite(db, user, dto); Assert.Empty(result.Conflicts);
        db.ChangeTracker.Clear(); var saved = await db.RentalAssets.SingleAsync(x => x.Id == asset.Id);
        Assert.Equal(0m, saved.PurchasePrice); Assert.Equal(0m, saved.MonthlyFee); Assert.Equal(0m, saved.SalePrice);
        Assert.Null(saved.BlackOverageUnitPrice); Assert.Equal(0m, saved.ColorOverageUnitPrice);
    }

    private static RentalAssetDto RentalAssetWriteCommand(RentalAsset asset)
    {
        var dto = asset.ToDto(); dto.ExpectedRevision = dto.Revision;
        dto.MutationId = Guid.NewGuid().ToString("N"); dto.MutationCreatedAtUtc = DateTime.UtcNow;
        dto.UpdatedAtUtc = DateTime.UtcNow.AddSeconds(1); return dto;
    }

    private async Task<SyncPushResult> PushRentalAssetWrite(거래플랜.Server.Api.Data.AppDbContext db, TestCurrentUserContext user, RentalAssetDto dto)
    {
        var response = await CreateController(db, user).Push(new SyncPushRequest {
            DeviceId = "rental-asset-amount-write", RentalAssets = [dto] }, CancellationToken.None);
        return Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
    }
}
