using System.Text.Json;
using System.Text.Json.Nodes;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

// Called for scope-validated commands inside the serialized sync transaction.
// Receipt hashing must continue to use the original, pre-calculation payload.
internal static class RentalProfileAmountWritePolicy
{
    private static readonly string[] EditableText =
        ["DisplayItemName", "Specification", "Unit", "MaterialNumber", "Note", "BillingLineMode", "IndividualGroupingMode"];

    public static async Task<string?> NormalizeAsync(AppDbContext db, ICurrentUserContext user,
        OfficeScopeService scope, RentalBillingProfileDto dto, RentalBillingProfile? existing, CancellationToken ct)
    {
        if (scope.CanViewSalesAmounts())
            return dto.AmountsHidden ? "비공개 금액이 포함되어 있습니다. 현재 권한으로 다시 조회한 뒤 저장해 주세요." : null;
        if (existing is not null && (existing.CustomerId != dto.CustomerId || existing.TenantCode != dto.TenantCode ||
            existing.OfficeCode != dto.OfficeCode || existing.ResponsibleOfficeCode != dto.ResponsibleOfficeCode))
            return "기존 청구의 거래처·업체·지점을 변경할 수 없습니다. 별도 권한이 있는 담당자가 확인해 주세요.";

        try
        {
            var incoming = ParseRows(dto.BillingTemplateJson);
            var previous = ParseRows(existing?.BillingTemplateJson ?? "[]");
            var oldById = incoming.Count == 0 ? new Dictionary<Guid, JsonObject>() : IndexRows(previous, "ItemId");
            _ = IndexRows(incoming, "ItemId");
            // Never accept a replacement financial history from a metadata edit.
            // Validate the submitted identities before replacing its redacted copy.
            var incomingRuns = ParseRows(dto.BillingRunsJson);
            var previousRuns = ParseRows(existing?.BillingRunsJson ?? "[]");
            var runsById = IndexRows(previousRuns, "RunId");
            _ = IndexRows(incomingRuns, "RunId");
            foreach (var run in incomingRuns)
            {
                if (!runsById.TryGetValue(Id(run, "RunId"), out var prior) ||
                    Text(run, "RunKey") != Text(prior, "RunKey"))
                    return "서버에 없는 청구 이력을 변경할 수 없습니다. 최신 청구를 다시 조회해 주세요.";
            }
            if (!RentalBillingRunTombstonePolicy.ValidateForAmountPrivacyRead(dto.BillingRunsJson).IsValid ||
                !RentalBillingRunTombstonePolicy.ValidateForServerMutation(existing?.BillingRunsJson ?? "[]").IsValid)
                return "청구 이력 형식 또는 저장된 금액을 확인할 수 없습니다.";

            var output = new JsonArray();
            var needsPrice = new List<(JsonObject Row, InvoiceLineDto Line)>();
            var unchanged = existing is not null && incoming.Count == previous.Count;
            foreach (var row in incoming)
            {
                var quantity = Number(row, "Quantity");
                if (quantity <= 0m || quantity >= 10000000000000000m)
                    return "품목 수량은 계산 가능한 양수여야 합니다.";
                oldById.TryGetValue(Id(row, "ItemId"), out var prior);
                var sameBasis = prior is not null && SamePriceBasis(prior, row);
                var merged = sameBasis ? (JsonObject)prior!.DeepClone() : new JsonObject();
                Set(merged, "ItemId", JsonValue.Create(Id(row, "ItemId")));
                foreach (var key in EditableText) Set(merged, key, JsonValue.Create(Text(row, key)));
                foreach (var key in new[] { "CatalogItemId", "RepresentativeAssetId", "IncludedAssetIds" })
                    Set(merged, key, Get(row, key)?.DeepClone());
                Set(merged, "Quantity", JsonValue.Create(quantity));
                if (sameBasis)
                {
                    var price = Number(prior!, "UnitPrice");
                    var oldAmount = Number(prior!, "Amount");
                    var oldQuantity = Number(prior!, "Quantity");
                    // A confirmed zero is a price/amount, not a missing value.
                    var amount = oldQuantity == quantity ? oldAmount : Math.Round(checked(price * quantity), 2, MidpointRounding.AwayFromZero);
                    CheckMoney(price); CheckMoney(amount);
                    Set(merged, "UnitPrice", JsonValue.Create(price));
                    Set(merged, "Amount", JsonValue.Create(amount));
                    unchanged &= oldQuantity == quantity;
                }
                else
                {
                    unchanged = false;
                    var catalogId = OptionalId(row, "CatalogItemId");
                    if (!catalogId.HasValue || !dto.CustomerId.HasValue)
                        return "새 품목의 서버 단가를 계산하려면 등록된 거래처와 품목을 선택해 주세요.";
                    // A catalog sales price is not an asset rental fee. Do not
                    // silently substitute it for a newly assigned asset's price.
                    if (OptionalId(row, "RepresentativeAssetId").HasValue ||
                        Get(row, "IncludedAssetIds") is JsonArray { Count: > 0 })
                        return "새 임대 자산의 계약 단가는 금액 권한이 있는 담당자가 확인해 주세요.";
                    needsPrice.Add((merged, new InvoiceLineDto
                    {
                        Id = Id(row, "ItemId"), ItemId = catalogId, Quantity = quantity,
                        ItemNameOriginal = Text(row, "DisplayItemName"), SpecificationOriginal = Text(row, "Specification"),
                        Unit = Text(row, "Unit"), UnitPrice = null, LineAmount = null
                    }));
                }
                output.Add(merged);
            }
            if (needsPrice.Count > 0)
            {
                var invoice = new InvoiceDto
                {
                    CustomerId = dto.CustomerId!.Value, TenantCode = dto.TenantCode, OfficeCode = dto.OfficeCode,
                    ResponsibleOfficeCode = dto.ResponsibleOfficeCode, VoucherType = VoucherType.Sales,
                    VatMode = InvoiceVatModes.Included, Lines = needsPrice.Select(x => x.Line).ToList(),
                    TotalAmount = null, SupplyAmount = null, VatAmount = null
                };
                var prices = await InvoiceAmountWritePolicy.NormalizeAsync(db, user, scope, invoice, null, ct);
                if (prices.Error is not null) return prices.Error;
                foreach (var (row, line) in needsPrice)
                {
                    Set(row, "UnitPrice", JsonValue.Create(DisclosedAmount.Require(line.UnitPrice)));
                    Set(row, "Amount", JsonValue.Create(DisclosedAmount.Require(line.LineAmount)));
                }
            }
            if (existing is null && output.Count == 0)
                return "신규 청구의 서버 단가를 계산할 품목을 선택해 주세요.";
            var monthly = unchanged ? existing!.MonthlyAmount : output.Sum(row => Number((JsonObject)row!, "Amount"));
            CheckMoney(monthly);
            // Mutate the command only after all validation/calculation succeeds.
            dto.MonthlyAmount = monthly;
            dto.DepositAmount = existing?.DepositAmount ?? 0m;
            dto.SettledAmount = existing?.SettledAmount ?? 0m;
            dto.OutstandingAmount = existing?.OutstandingAmount ?? 0m;
            dto.BillingTemplateJson = output.ToJsonString();
            dto.BillingRunsJson = existing?.BillingRunsJson ?? "[]";
            if (existing is not null)
            {
                dto.BillingStatus = existing.BillingStatus;
                dto.SettlementStatus = existing.SettlementStatus;
                dto.CompletionStatus = existing.CompletionStatus;
                dto.LastBilledDate = existing.LastBilledDate;
                dto.LastSettledDate = existing.LastSettledDate;
                dto.RequiresFollowUp = existing.RequiresFollowUp;
            }
            return null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or OverflowException or FormatException or ArgumentException)
        {
            return "품목·수량 또는 청구 이력 형식이 올바르지 않아 서버 금액을 계산할 수 없습니다.";
        }
    }

    private static bool SamePriceBasis(JsonObject a, JsonObject b)
        => OptionalId(a, "CatalogItemId") == OptionalId(b, "CatalogItemId") &&
           OptionalId(a, "RepresentativeAssetId") == OptionalId(b, "RepresentativeAssetId") &&
           UnitCatalogNormalizer.Normalize(Text(a, "Unit")) == UnitCatalogNormalizer.Normalize(Text(b, "Unit")) &&
           Text(a, "Specification") == Text(b, "Specification") &&
           Text(a, "MaterialNumber") == Text(b, "MaterialNumber") &&
           JsonNode.DeepEquals(Get(a, "IncludedAssetIds"), Get(b, "IncludedAssetIds"));

    private static List<JsonObject> ParseRows(string? raw)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(raw) ? "[]" : raw);
        CheckDuplicateNames(doc.RootElement);
        if (JsonNode.Parse(doc.RootElement.GetRawText()) is not JsonArray array || array.Count > 512)
            throw new JsonException();
        return array.Select(n => n as JsonObject ?? throw new JsonException()).ToList();
    }
    private static void CheckDuplicateNames(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in node.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new JsonException();
                CheckDuplicateNames(p.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var child in node.EnumerateArray()) CheckDuplicateNames(child);
    }
    private static Dictionary<Guid, JsonObject> IndexRows(List<JsonObject> rows, string key)
        => rows.ToDictionary(x => Id(x, key));
    private static Guid Id(JsonObject row, string key)
        => OptionalId(row, key) ?? throw new JsonException();
    private static Guid? OptionalId(JsonObject row, string key)
    {
        var node = Get(row, key);
        if (node is null) return null;
        var id = node.GetValue<Guid>();
        return id == Guid.Empty ? throw new JsonException() : id;
    }
    private static decimal Number(JsonObject row, string key)
        => Get(row, key)?.GetValue<decimal>() ?? throw new JsonException();
    private static string Text(JsonObject row, string key)
        => Get(row, key)?.GetValue<string>() ?? string.Empty;
    private static JsonNode? Get(JsonObject row, string key)
        => row.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
    private static void Set(JsonObject row, string key, JsonNode? value)
    {
        foreach (var name in row.Select(p => p.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToArray())
            row.Remove(name);
        row[key] = value;
    }
    private static void CheckMoney(decimal value)
    {
        if (value < 0m || value >= 10000000000000000m) throw new OverflowException();
    }
}
