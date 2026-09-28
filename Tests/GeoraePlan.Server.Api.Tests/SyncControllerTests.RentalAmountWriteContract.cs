using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed partial class SyncControllerTests
{
    // Release contracts: non-money edits must reach the server without trusting
    // client prices, and an exact retry must not apply the edit twice.
    [Theory]
    [InlineData("all-hidden", false)]
    [InlineData("forged", false)]
    [InlineData("scalar-hidden", false)]
    [InlineData("all-hidden", true)]
    [InlineData("forged", true)]
    [InlineData("scalar-hidden", true)]
    public async Task RentalAmountWriteContract_MetadataAndQuantityUseServerPrice(
        string payloadMode, bool zeroPrice)
    {
        var redacted = payloadMode != "forged";
        var profile = await SeedRentalAmountWriteContractAsync(zeroPrice);
        var originalRuns = JsonNode.Parse(profile.BillingRunsJson);
        var dto = profile.ToDto();
        dto.ExpectedRevision = profile.Revision;
        dto.MutationId = "rental-non-money-" + Guid.NewGuid().ToString("N");
        dto.UpdatedAtUtc = profile.UpdatedAtUtc.AddMinutes(1);
        dto.Notes = "직원 비고 저장";
        var template = JsonNode.Parse(dto.BillingTemplateJson)!.AsArray();
        template[0]!["DisplayItemName"] = "변경된 표시 품목";
        template[0]!["Quantity"] = 3;
        template[0]!["Note"] = "품목 비고";
        template[0]!["UnitPrice"] = redacted ? null : JsonValue.Create(7m);
        template[0]!["Amount"] = redacted ? null : JsonValue.Create(21m);
        dto.BillingTemplateJson = template.ToJsonString();
        dto.MonthlyAmount = redacted ? null : 21;
        dto.DepositAmount = redacted ? null : 999999;
        dto.SettledAmount = redacted ? null : 999999;
        dto.OutstandingAmount = redacted ? null : 999999;
        if (payloadMode == "all-hidden")
        {
            var runs = JsonNode.Parse(dto.BillingRunsJson)!.AsArray();
            runs[0]!["BilledAmount"] = null;
            runs[0]!["SettledAmount"] = null;
            runs[0]!["Items"]![0]!["UnitPrice"] = null;
            runs[0]!["Items"]![0]!["Amount"] = null;
            dto.BillingRunsJson = runs.ToJsonString();
        }
        var originalPayload = JsonSerializer.Serialize(dto);
        var user = RentalAmountWriteContractUser();
        await using var db = CreateDbContext(user);
        var controller = CreateController(db, user);
        var response = await controller.Push(new SyncPushRequest
        { DeviceId = "rental-amount-contract", RentalBillingProfiles = [dto] }, CancellationToken.None);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.True(result.ConflictCount == 0, string.Join(" | ", result.Conflicts.Select(x => x.Reason)));
        Assert.Equal(1, result.AcceptedCount);
        db.ChangeTracker.Clear();
        var saved = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(x => x.Id == profile.Id);
        var savedTemplate = JsonNode.Parse(saved.BillingTemplateJson)!.AsArray();
        Assert.Equal(zeroPrice ? 0m : 150000m, saved.MonthlyAmount);
        Assert.Equal(10000m, saved.DepositAmount);
        Assert.Equal(zeroPrice ? 0m : 50000m, savedTemplate[0]!["UnitPrice"]!.GetValue<decimal>());
        Assert.Equal(zeroPrice ? 0m : 150000m, savedTemplate[0]!["Amount"]!.GetValue<decimal>());
        Assert.Equal(3m, savedTemplate[0]!["Quantity"]!.GetValue<decimal>());
        Assert.Equal("변경된 표시 품목", savedTemplate[0]!["DisplayItemName"]!.GetValue<string>());
        Assert.Equal("품목 비고", savedTemplate[0]!["Note"]!.GetValue<string>());
        Assert.Equal(dto.Notes, saved.Notes);
        Assert.True(JsonNode.DeepEquals(originalRuns, JsonNode.Parse(saved.BillingRunsJson)));
        var revision = saved.Revision;
        var replay = await controller.Push(new SyncPushRequest
        {
            DeviceId = "rental-amount-contract",
            RentalBillingProfiles = [JsonSerializer.Deserialize<RentalBillingProfileDto>(originalPayload)!]
        }, CancellationToken.None);
        Assert.Empty(Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(replay.Result).Value).Conflicts);
        db.ChangeTracker.Clear();
        Assert.Equal(revision, (await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(x => x.Id == profile.Id)).Revision);
        Assert.Single(await db.ProcessedSyncMutations.Where(x => x.MutationId == dto.MutationId).ToListAsync());
        var alteredReplay = JsonSerializer.Deserialize<RentalBillingProfileDto>(originalPayload)!;
        alteredReplay.Notes = "same mutation with another command";
        var altered = await controller.Push(new SyncPushRequest
        { DeviceId = "rental-amount-contract", RentalBillingProfiles = [alteredReplay] }, CancellationToken.None);
        Assert.NotEmpty(Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(altered.Result).Value).Conflicts);
        db.ChangeTracker.Clear();
        Assert.Equal(revision, (await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(x => x.Id == profile.Id)).Revision);
    }

    [Theory]
    [InlineData("other-office")]
    [InlineData("other-tenant")]
    [InlineData("stale")]
    [InlineData("no-edit")]
    public async Task RentalAmountWriteContract_ExistingScopeAndConcurrencyGuardsRemain(string denied)
    {
        var profile = await SeedRentalAmountWriteContractAsync(false);
        var before = JsonSerializer.Serialize(profile.ToDto());
        var dto = profile.ToDto();
        dto.ExpectedRevision = denied == "stale" ? profile.Revision + 1 : profile.Revision;
        dto.MutationId = "rental-denied-" + Guid.NewGuid().ToString("N");
        dto.UpdatedAtUtc = profile.UpdatedAtUtc.AddMinutes(1);
        dto.Notes = "must not persist";
        var user = RentalAmountWriteContractUser(denied);
        await using var db = CreateDbContext(user);
        var response = await CreateController(db, user).Push(new SyncPushRequest
        { DeviceId = "rental-amount-contract", RentalBillingProfiles = [dto] }, CancellationToken.None);
        if (denied == "no-edit")
            Assert.Equal(403, Assert.IsType<ObjectResult>(response.Result).StatusCode);
        else
        {
            var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
            Assert.Equal(0, result.AcceptedCount);
            Assert.NotEmpty(result.Conflicts);
            foreach (var conflict in result.Conflicts)
                foreach (var snapshot in new[] { conflict.ClientJson, conflict.ServerJson }.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    var json = JsonNode.Parse(snapshot)!;
                    Assert.Null(json["MonthlyAmount"]);
                    Assert.Null(json["DepositAmount"]);
                    if (json["BillingTemplateJson"] is { } templateJson)
                        foreach (var item in JsonNode.Parse(templateJson.GetValue<string>())!.AsArray())
                        {
                            Assert.Null(item!["Amount"]);
                            Assert.Null(item["UnitPrice"]);
                        }
                }
        }
        db.ChangeTracker.Clear();
        Assert.Equal(before, JsonSerializer.Serialize((await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(x => x.Id == profile.Id)).ToDto()));
        Assert.False(await db.ProcessedSyncMutations.AnyAsync(x => x.MutationId == dto.MutationId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RentalAmountWriteContract_PullHidesScalarAndNestedMoneyWithoutChangingStorage(bool administrationOnly)
    {
        var profile = await SeedRentalAmountWriteContractAsync(false);
        var before = JsonSerializer.Serialize(profile.ToDto());
        var user = RentalAmountWriteContractUser();
        await using var db = CreateDbContext(user);
        var response = await CreateController(db, user).Pull(0, CancellationToken.None,
            rentalAdministrationOnly: administrationOnly,
            rentalBillingScheduleVersion: RentalBillingScheduleRules.ScheduleCapabilityVersion);
        var pulled = Assert.IsType<SyncPullResponse>(Assert.IsType<OkObjectResult>(response.Result).Value);
        var dto = Assert.Single(pulled.RentalBillingProfiles, x => x.Id == profile.Id);
        Assert.Null(dto.MonthlyAmount);
        Assert.Null(dto.DepositAmount);
        Assert.Null(dto.SettledAmount);
        Assert.Null(dto.OutstandingAmount);
        var template = JsonNode.Parse(dto.BillingTemplateJson)!.AsArray();
        Assert.Null(template[0]!["UnitPrice"]);
        Assert.Null(template[0]!["Amount"]);
        Assert.Equal(2m, template[0]!["Quantity"]!.GetValue<decimal>());
        var runs = JsonNode.Parse(dto.BillingRunsJson)!.AsArray();
        Assert.Null(runs[0]!["BilledAmount"]);
        Assert.Null(runs[0]!["SettledAmount"]);
        Assert.Null(runs[0]!["Items"]![0]!["UnitPrice"]);
        Assert.Null(runs[0]!["Items"]![0]!["Amount"]);
        Assert.Equal(before, JsonSerializer.Serialize((await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == profile.Id)).ToDto()));
    }

    [Theory]
    [InlineData("duplicate-row")]
    [InlineData("duplicate-property")]
    [InlineData("foreign-run")]
    [InlineData("negative-quantity")]
    [InlineData("overflow")]
    [InlineData("missing-price-source")]
    public async Task RentalAmountWriteContract_InvalidCommandsCannotChangeStoredFinancialState(string invalid)
    {
        var profile = await SeedRentalAmountWriteContractAsync(false);
        var before = JsonSerializer.Serialize(profile.ToDto());
        var dto = profile.ToDto();
        dto.MutationId = Guid.NewGuid().ToString("N");
        dto.ExpectedRevision = profile.Revision;
        dto.UpdatedAtUtc = profile.UpdatedAtUtc.AddMinutes(1);
        var rows = JsonNode.Parse(dto.BillingTemplateJson)!.AsArray();
        if (invalid == "duplicate-row") rows.Add(rows[0]!.DeepClone());
        if (invalid == "negative-quantity") rows[0]!["Quantity"] = -1;
        if (invalid == "overflow") rows[0]!["Quantity"] = decimal.MaxValue;
        if (invalid == "missing-price-source") rows[0]!["ItemId"] = Guid.NewGuid();
        dto.BillingTemplateJson = rows.ToJsonString();
        if (invalid == "duplicate-property") dto.BillingTemplateJson = dto.BillingTemplateJson.Replace("\"Quantity\":2", "\"quantity\":3,\"Quantity\":2", StringComparison.Ordinal);
        if (invalid == "foreign-run")
        {
            var runs = JsonNode.Parse(dto.BillingRunsJson)!.AsArray();
            runs[0]!["RunId"] = Guid.NewGuid();
            dto.BillingRunsJson = runs.ToJsonString();
        }
        var user = RentalAmountWriteContractUser();
        await using var db = CreateDbContext(user);
        var response = await CreateController(db, user).Push(new SyncPushRequest
        { DeviceId = "rental-invalid", RentalBillingProfiles = [dto] }, CancellationToken.None);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        Assert.NotEmpty(result.Conflicts);
        Assert.Equal(0, result.AcceptedCount);
        db.ChangeTracker.Clear();
        Assert.Equal(before, JsonSerializer.Serialize((await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(x => x.Id == profile.Id)).ToDto()));
        Assert.False(await db.ProcessedSyncMutations.AnyAsync(x => x.MutationId == dto.MutationId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RentalAmountWriteContract_NewCatalogRowsUseScopedServerPrices(bool create, bool foreignItem)
    {
        var customer = new Customer { Id = Guid.NewGuid(), TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet, ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "단가 계산 거래처", NameMatchKey = "RENTALPRICINGCUSTOMER", TradeType = "매출" };
        var item = new Item { Id = Guid.NewGuid(), TenantCode = foreignItem ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
            OfficeCode = foreignItem ? OfficeCodeCatalog.Itworld : OfficeCodeCatalog.Usenet,
            NameOriginal = "등록 서비스", NameMatchKey = "RENTALCATALOGSERVICE", Unit = "EA", SalePrice = 1100,
            TrackingType = ItemTrackingTypes.NonStock };
        _dbContext.AddRange(customer, item, new RentalManagementCompany
        { Code = OfficeCodeCatalog.Usenet, Name = "유즈넷", TenantCode = TenantScopeCatalog.UsenetGroup });
        await _dbContext.SaveChangesAsync();
        RentalBillingProfile? profile = null;
        if (!create)
        {
            profile = await SeedRentalAmountWriteContractAsync(false);
            _dbContext.Attach(profile);
            profile.CustomerId = customer.Id;
            await _dbContext.SaveChangesAsync();
            _dbContext.ChangeTracker.Clear();
        }
        var dto = profile?.ToDto() ?? new RentalBillingProfileDto
        {
            Id = Guid.NewGuid(), ProfileKey = "new-catalog-" + Guid.NewGuid().ToString("N"),
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet
        };
        dto.CustomerId = customer.Id;
        dto.ExpectedRevision = profile?.Revision ?? 0;
        dto.UpdatedAtUtc = (profile?.UpdatedAtUtc ?? DateTime.UtcNow).AddMinutes(1);
        dto.MutationId = Guid.NewGuid().ToString("N");
        dto.BillingTemplateJson = JsonSerializer.Serialize(new[] { new {
            ItemId = Guid.NewGuid(), CatalogItemId = item.Id, DisplayItemName = item.NameOriginal,
            Unit = "EA", Quantity = 3m, UnitPrice = (decimal?)null, Amount = (decimal?)null, Note = "신규 품목 비고"
        } });
        dto.MonthlyAmount = dto.DepositAmount = dto.SettledAmount = dto.OutstandingAmount = null;
        var user = RentalAmountWriteContractUser();
        await using var db = CreateDbContext(user);
        var response = await CreateController(db, user).Push(new SyncPushRequest
        { DeviceId = "new-catalog", RentalBillingProfiles = [dto] }, CancellationToken.None);
        var result = Assert.IsType<SyncPushResult>(Assert.IsType<OkObjectResult>(response.Result).Value);
        if (foreignItem)
        {
            Assert.Equal(0, result.AcceptedCount);
            Assert.NotEmpty(result.Conflicts);
            return;
        }
        Assert.True(result.ConflictCount == 0, string.Join(" | ", result.Conflicts.Select(x => x.Reason)));
        Assert.Equal(1, result.AcceptedCount);
        db.ChangeTracker.Clear();
        var saved = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(x => x.Id == dto.Id);
        Assert.Equal(3300m, saved.MonthlyAmount);
        Assert.Equal(1100m, JsonNode.Parse(saved.BillingTemplateJson)![0]!["UnitPrice"]!.GetValue<decimal>());
        Assert.Equal(item.SalePrice, (await db.Items.IgnoreQueryFilters().SingleAsync(x => x.Id == item.Id)).SalePrice);
    }

    private static TestCurrentUserContext RentalAmountWriteContractUser(string denied = "") => new()
    {
        Username = "rental-no-money", TenantCode = denied == "other-tenant" ? TenantScopeCatalog.Itworld : TenantScopeCatalog.UsenetGroup,
        OfficeCode = denied == "other-tenant" ? OfficeCodeCatalog.Itworld : denied == "other-office" ? OfficeCodeCatalog.Yeonsu : OfficeCodeCatalog.Usenet,
        ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
        Permissions = denied == "no-edit" ? [] : [PermissionNames.RentalProfileEdit]
    };

    private async Task<RentalBillingProfile> SeedRentalAmountWriteContractAsync(bool zeroPrice, AppDbContext? context = null)
    {
        var db = context ?? _dbContext;
        var amount = zeroPrice ? 0m : 100000m;
        var items = new[] { new { ItemId = Guid.NewGuid(), DisplayItemName = "표시 품목", Quantity = 2m,
            UnitPrice = zeroPrice ? 0m : 50000m, Amount = amount, Note = "기존 비고", IncludedAssetIds = Array.Empty<Guid>() } };
        var profile = new RentalBillingProfile
        {
            ProfileKey = "rental-amount-contract-" + Guid.NewGuid().ToString("N"),
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet, CustomerName = "격리 금액 권한 시험",
            ItemName = "표시 품목", MonthlyAmount = amount, DepositAmount = 10000,
            BillingCycleMonths = 1, BillingDay = 25, BillingAdvanceMode = "후불", IsActive = true,
            BillingTemplateJson = JsonSerializer.Serialize(items),
            BillingRunsJson = JsonSerializer.Serialize(new[] { new {
                RunId = Guid.NewGuid(), RunKey = "2026-08", ScheduledDate = "2026-08-25",
                PeriodStartDate = "2026-08-01", PeriodEndDate = "2026-08-31", CycleMonths = 1,
                PeriodLabel = "2026-08", Status = "청구중", BilledAmount = amount,
                SettledAmount = 0m, SettlementStatus = "미입금", Items = items
            } })
        };
        Assert.True(RentalBillingRunTombstonePolicy.ValidateForServerMutation(profile.BillingRunsJson).IsValid);
        db.RentalBillingProfiles.Add(profile);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == profile.Id);
    }
}
