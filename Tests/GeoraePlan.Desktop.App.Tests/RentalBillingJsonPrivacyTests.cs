using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingJsonPrivacyTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public void PrivacyReadValidation_DoesNotRelaxMutationOrIdentityValidation()
    {
        var id = Guid.NewGuid();
        var json = "[{\"RunId\":\"" + id + "\",\"RunKey\":\"2026-09\",\"BilledAmount\":null,\"SettledAmount\":null}]";
        Assert.True(RentalBillingRunTombstonePolicy.ValidateForAmountPrivacyRead(json).IsValid);
        Assert.False(RentalBillingRunTombstonePolicy.Validate(json).IsValid);
        Assert.False(RentalBillingRunTombstonePolicy.ValidateForServerMutation(json).IsValid);
        Assert.False(RentalBillingRunTombstonePolicy.ValidateForFinancialRecalculation(json).IsValid);
        foreach (var malformed in new[] {
            json.Replace("\"BilledAmount\":null", "\"BilledAmount\":-1"),
            json.Replace("\"BilledAmount\":null", "\"BilledAmount\":\"unknown\""),
            json.Replace("\"RunKey\":\"2026-09\"", "\"RunKey\":\"2026-09\",\"runKey\":\"other\""),
            json.Replace("\"BilledAmount\":null", "\"BilledAmount\":null,\"Status\":\"invalid\"") })
            Assert.False(RentalBillingRunTombstonePolicy.ValidateForAmountPrivacyRead(malformed).IsValid);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("asset")]
    [InlineData("template")]
    public void StartupMoneyRepair_PreservesHiddenTemplates(string hiddenSource)
    {
        using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        var asset = new LocalRentalAsset { Id = Guid.NewGuid(), MonthlyFee = 120, SalesAmountsHidden = hiddenSource == "asset" };
        var item = new RentalBillingTemplateItemModel { IncludedAssetIds = [asset.Id], UnitPrice = hiddenSource == "template" ? null : 120, Amount = hiddenSource == "template" ? null : 120 };
        var profile = new LocalRentalBillingProfile { MonthlyAmount = 456, AmountsHidden = hiddenSource == "profile", BillingTemplateJson = JsonSerializer.Serialize(new[] { item }, JsonOptions) };
        var original = profile.BillingTemplateJson;
        var method = typeof(LocalDbInitializer).GetMethod("TryNormalizeBillingTemplateFromLinkedAssets", BindingFlags.Static | BindingFlags.NonPublic)!;
        object?[] args = [profile, new[] { asset }, new RentalStateService(db), null, null];
        Assert.False((bool)method.Invoke(null, args)!);
        Assert.Equal(original, args[3]); Assert.Equal(456m, args[4]);
        Assert.Equal(original, profile.BillingTemplateJson);
    }

    [Fact]
    public async Task IntegrityScan_KeepsLinkDiagnosticsButDoesNotTreatUnknownMoneyAsMismatch()
    {
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(); await db.Database.EnsureCreatedAsync();
        var item = new RentalBillingTemplateItemModel { DisplayItemName = "keep missing link", IncludedAssetIds = [Guid.NewGuid()], UnitPrice = null, Amount = null };
        var run = new RentalBillingRunModel { RunId = Guid.NewGuid(), RunKey = "2026-09", BilledAmount = null, SettledAmount = null, Items = [item] };
        var profile = new LocalRentalBillingProfile { Id = Guid.NewGuid(), ProfileKey = "json-privacy", CustomerName = "privacy metadata", MonthlyAmount = 999, IsDirty = false,
            BillingTemplateJson = JsonSerializer.Serialize(new[] { item }, JsonOptions), BillingRunsJson = JsonSerializer.Serialize(new[] { run }) };
        db.Add(profile); await db.SaveChangesAsync();
        var before = profile.BillingRunsJson;
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { UserId = Guid.NewGuid(), Username = "privacy", Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup, OfficeCode = OfficeCodeCatalog.Usenet, ScopeType = TenantScopeCatalog.ScopeAdmin });
        var result = await new DataIntegrityIssueService(db).ScanAsync(session);
        Assert.Contains(result.Issues, x => x.Code == DataIntegrityIssueCodes.RentalTemplateMissingAsset);
        Assert.DoesNotContain(result.Issues, x => x.Code == DataIntegrityIssueCodes.RentalBillingTemplateInvalid ||
            x.Code == DataIntegrityIssueCodes.RentalProfileMonthlyAmountMismatch || x.Code == DataIntegrityIssueCodes.RentalBillingRunSettlementMismatch);
        Assert.Equal(before, profile.BillingRunsJson); Assert.False(profile.IsDirty);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TemplateAndRunJson_DistinguishUnknownFromZero(bool hidden)
    {
        var item = new RentalBillingTemplateItemModel { UnitPrice = hidden ? null : 0m, Amount = hidden ? null : 0m, Quantity = 2, Note = "keep note" };
        var run = new RentalBillingRunModel { BilledAmount = hidden ? null : 0m, SettledAmount = hidden ? null : 0m, Items = [item] };
        var raw = JsonSerializer.Serialize(run, JsonOptions);
        using var document = JsonDocument.Parse(raw);
        Assert.Equal(hidden ? JsonValueKind.Null : JsonValueKind.Number, document.RootElement.GetProperty("BilledAmount").ValueKind);
        Assert.Equal(hidden ? JsonValueKind.Null : JsonValueKind.Number, document.RootElement.GetProperty("SettledAmount").ValueKind);
        var jsonItem = document.RootElement.GetProperty("Items")[0];
        Assert.Equal(hidden ? JsonValueKind.Null : JsonValueKind.Number, jsonItem.GetProperty("UnitPrice").ValueKind);
        Assert.Equal(hidden ? JsonValueKind.Null : JsonValueKind.Number, jsonItem.GetProperty("Amount").ValueKind);
        var restored = JsonSerializer.Deserialize<RentalBillingRunModel>(raw, JsonOptions)!;
        Assert.Equal(hidden, restored.AmountsHidden);
        Assert.Equal(hidden, Assert.Single(restored.Items).AmountsHidden);
        Assert.Equal(2m, restored.Items[0].Quantity);
        Assert.Equal("keep note", restored.Items[0].Note);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Readers_PreserveMetadataWithExplicitNullOrHiddenStoredMoney(bool hideStoredMoney)
    {
        using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        var service = new RentalStateService(db);
        var itemId = Guid.NewGuid(); var catalogId = Guid.NewGuid(); var assetId = Guid.NewGuid(); var runId = Guid.NewGuid();
        var item = new RentalBillingTemplateItemModel { ItemId = itemId, CatalogItemId = catalogId, DisplayItemName = "printer", Quantity = 3,
            UnitPrice = hideStoredMoney ? 120m : null, Amount = hideStoredMoney ? 360m : null, Note = "keep note", IncludedAssetIds = [assetId], RepresentativeAssetId = assetId, BillingLineMode = "묶음" };
        var run = new RentalBillingRunModel { RunId = runId, RunKey = "2026-09", PeriodLabel = "2026-09", BilledAmount = hideStoredMoney ? 360m : null,
            SettledAmount = hideStoredMoney ? 100m : null, Note = "run note", Items = [item] };
        var profile = new LocalRentalBillingProfile { AmountsHidden = hideStoredMoney, MonthlyAmount = 999, BillingType = "묶음",
            BillingTemplateJson = JsonSerializer.Serialize(new[] { item }, JsonOptions), BillingRunsJson = JsonSerializer.Serialize(new[] { run }) };
        var templateBefore = profile.BillingTemplateJson; var runsBefore = profile.BillingRunsJson;
        var parsedItem = Assert.Single(service.GetBillingTemplateItems(profile));
        Assert.Equal(itemId, parsedItem.ItemId); Assert.Equal(catalogId, parsedItem.CatalogItemId);
        Assert.Equal(assetId, parsedItem.RepresentativeAssetId); Assert.Equal(assetId, Assert.Single(parsedItem.IncludedAssetIds));
        Assert.Equal(3m, parsedItem.Quantity); Assert.Equal("keep note", parsedItem.Note);
        Assert.Null(parsedItem.UnitPrice); Assert.Null(parsedItem.Amount);
        var runJson = hideStoredMoney ? LocalMappings.ToDto(profile).BillingRunsJson : profile.BillingRunsJson;
        var validation = RentalBillingRunTombstonePolicy.ValidateForAmountPrivacyRead(runJson);
        Assert.True(validation.IsValid, validation.Error);
        Assert.Single(JsonSerializer.Deserialize<List<RentalBillingRunModel>>(runJson, JsonOptions)!);
        var parsedRun = Assert.Single(service.GetBillingRuns(profile));
        Assert.Equal(runId, parsedRun.RunId); Assert.Equal("2026-09", parsedRun.RunKey); Assert.Equal("run note", parsedRun.Note);
        Assert.Null(parsedRun.BilledAmount); Assert.Null(parsedRun.SettledAmount);
        Assert.Equal(itemId, Assert.Single(parsedRun.Items).ItemId); Assert.Null(parsedRun.Items[0].Amount);
        Assert.Equal(templateBefore, profile.BillingTemplateJson); Assert.Equal(runsBefore, profile.BillingRunsJson);
        var reserialized = JsonSerializer.Serialize(parsedRun, JsonOptions);
        Assert.True(JsonSerializer.Deserialize<RentalBillingRunModel>(reserialized, JsonOptions)!.AmountsHidden);
    }

    [Fact]
    public void HiddenProfileOutbound_PreservesCaseMetadataExtensionsAndTombstonesWithoutMoney()
    {
        var id = Guid.NewGuid();
        var profile = new LocalRentalBillingProfile { AmountsHidden = true,
            BillingTemplateJson = "[{\"displayItemName\":\"printer\",\"quantity\":3,\"unitPrice\":120,\"AMOUNT\":360,\"Note\":\"keep\",\"Extension\":{\"Version\":2}}]",
            BillingRunsJson = "[{\"RunId\":\"" + id + "\",\"RunKey\":\"2026-09\",\"IsTombstoned\":true,\"BilledAmount\":360,\"settledAmount\":100,\"Items\":[{\"Quantity\":3,\"Note\":\"keep item\"}]}]" };
        var templateBefore = profile.BillingTemplateJson; var runsBefore = profile.BillingRunsJson;
        var wire = LocalMappings.ToDto(profile);
        using var template = JsonDocument.Parse(wire.BillingTemplateJson);
        var item = template.RootElement[0];
        Assert.Equal(JsonValueKind.Null, item.GetProperty("unitPrice").ValueKind);
        Assert.Equal(JsonValueKind.Null, item.GetProperty("AMOUNT").ValueKind);
        Assert.Equal(3, item.GetProperty("quantity").GetInt32());
        Assert.Equal("keep", item.GetProperty("Note").GetString());
        Assert.Equal(2, item.GetProperty("Extension").GetProperty("Version").GetInt32());
        using var runs = JsonDocument.Parse(wire.BillingRunsJson);
        var run = runs.RootElement[0];
        Assert.Equal(id, run.GetProperty("RunId").GetGuid()); Assert.True(run.GetProperty("IsTombstoned").GetBoolean());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("BilledAmount").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("settledAmount").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("Items")[0].GetProperty("UnitPrice").ValueKind);
        Assert.Equal(JsonValueKind.Null, run.GetProperty("Items")[0].GetProperty("Amount").ValueKind);
        Assert.Equal(templateBefore, profile.BillingTemplateJson); Assert.Equal(runsBefore, profile.BillingRunsJson);
        profile.AmountsHidden = false;
        Assert.Equal(templateBefore, LocalMappings.ToDto(profile).BillingTemplateJson);
        Assert.Equal(runsBefore, LocalMappings.ToDto(profile).BillingRunsJson);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")] 
    [InlineData("[null]")]
    [InlineData("[123]")]
    [InlineData("[{\"Items\":{}}]")]
    public void MalformedHiddenJson_BlocksSendingWithoutDiscardingOriginal(string json)
    {
        var profile = new LocalRentalBillingProfile { AmountsHidden = true, BillingRunsJson = json };
        Assert.Throws<InvalidOperationException>(() => LocalMappings.ToDto(profile));
        Assert.Equal(json, profile.BillingRunsJson);
    }

    [Fact]
    public void HiddenFallbackAndRunClone_PreserveUnknownRatherThanCalculateZero()
    {
        using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
        var service = new RentalStateService(db);
        var item = Assert.Single(service.GetBillingTemplateItems(new LocalRentalBillingProfile { AmountsHidden = true, MonthlyAmount = 123 }));
        Assert.Null(item.UnitPrice); Assert.Null(item.Amount);
        var cloneMethod = typeof(RentalStateService).GetMethod("CloneTemplateItemsForRun", BindingFlags.Static | BindingFlags.NonPublic)!;
        var clone = Assert.Single((List<RentalBillingTemplateItemModel>)cloneMethod.Invoke(null, [new[] { item }, 1])!);
        Assert.Null(clone.UnitPrice); Assert.Null(clone.Amount);
        var calculate = typeof(RentalStateService).GetMethod("ResolveTemplateMonthlyAmount", BindingFlags.Static | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() => calculate.Invoke(null, [item]));
        Assert.IsType<InvalidOperationException>(error.InnerException);
    }
}
