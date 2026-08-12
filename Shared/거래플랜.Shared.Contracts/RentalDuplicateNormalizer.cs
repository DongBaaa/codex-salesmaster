using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace 거래플랜.Shared.Contracts;

public static class RentalDuplicateNormalizer
{
    private const string SourceManagementIdLabel = "원본 관리ID";
    private const string SourceManagementNumberLabel = "원본 관리번호";

    private static readonly Regex SourceManagementIdRegex = new(
        Regex.Escape(SourceManagementIdLabel) + @"\s*:\s*([^\s\r\n]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SourceManagementNumberRegex = new(
        Regex.Escape(SourceManagementNumberLabel) + @"\s*:\s*([^\r\n]+?)(?=\s+(?:K\S*|C\S*|기타사항|회수\d|렌탈\d)|\r|\n|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string NormalizeTextKey(string? value)
        => RentalCatalogValueNormalizer.NormalizeLooseKey(value);

    public static string NormalizeTrimmed(string? value)
        => (value ?? string.Empty).Trim();

    public static string NormalizeProfileKeyPart(string? value)
        => new string((value ?? string.Empty)
            .Trim()
            .ToUpperInvariant()
            .Where(ch => !char.IsWhiteSpace(ch) && ch != '[' && ch != ']')
            .ToArray());

    public static string BuildProfileKey(
        string? managementCompanyCode,
        Guid? customerId,
        string? businessNumber,
        string? customerName,
        string? billingType,
        string? billingAdvanceMode,
        int billingDay,
        int billingCycleMonths,
        string? billingMethod)
        => BuildProfileKeyCore(
            managementCompanyCode,
            customerId,
            businessNumber,
            customerName,
            billingType,
            billingAdvanceMode,
            billingDay,
            billingCycleMonths,
            billingMethod,
            includeCustomerNameWithCustomerId: true);

    public static string BuildLegacyProfileKey(
        string? managementCompanyCode,
        Guid? customerId,
        string? businessNumber,
        string? customerName,
        string? billingType,
        string? billingAdvanceMode,
        int billingDay,
        int billingCycleMonths,
        string? billingMethod)
        => BuildProfileKeyCore(
            managementCompanyCode,
            customerId,
            businessNumber,
            customerName,
            billingType,
            billingAdvanceMode,
            billingDay,
            billingCycleMonths,
            billingMethod,
            includeCustomerNameWithCustomerId: false);

    private static string BuildProfileKeyCore(
        string? managementCompanyCode,
        Guid? customerId,
        string? businessNumber,
        string? customerName,
        string? billingType,
        string? billingAdvanceMode,
        int billingDay,
        int billingCycleMonths,
        string? billingMethod,
        bool includeCustomerNameWithCustomerId)
    {
        string ownerKey;
        if (customerId.HasValue && customerId.Value != Guid.Empty)
        {
            ownerKey = $"CUSTOMER:{customerId.Value:N}";
            var normalizedCustomerName = NormalizeProfileKeyPart(customerName);
            if (includeCustomerNameWithCustomerId && !string.IsNullOrWhiteSpace(normalizedCustomerName))
                ownerKey = string.Join('|', ownerKey, $"NAME:{normalizedCustomerName}");
        }
        else
        {
            ownerKey = string.Join('|',
                NormalizeProfileKeyPart(businessNumber),
                NormalizeProfileKeyPart(customerName));
        }

        return string.Join('|',
            NormalizeProfileKeyPart(managementCompanyCode),
            ownerKey,
            NormalizeProfileKeyPart(billingType),
            NormalizeProfileKeyPart(billingAdvanceMode),
            billingDay.ToString(CultureInfo.InvariantCulture),
            billingCycleMonths.ToString(CultureInfo.InvariantCulture),
            NormalizeProfileKeyPart(billingMethod));
    }

    public static string ExtractImportedManagementId(string? notes)
        => ExtractIdentifier(notes, SourceManagementIdRegex);

    public static string ExtractImportedManagementNumber(string? notes)
        => ExtractIdentifier(notes, SourceManagementNumberRegex);

    public static string BuildRentalAssetDuplicateKey(
        string? customerName,
        string? currentCustomerName,
        string? installSiteName,
        string? installLocation,
        string? itemCategoryName,
        string? itemName,
        string? manufacturer,
        string? machineNumber,
        decimal monthlyFee,
        int contractMonths,
        string? assetStatus)
    {
        return string.Join('|',
            NormalizeTextKey(customerName),
            NormalizeTextKey(string.IsNullOrWhiteSpace(currentCustomerName) ? customerName : currentCustomerName),
            NormalizeTextKey(string.IsNullOrWhiteSpace(installLocation) ? installSiteName : installLocation),
            NormalizeTextKey(itemCategoryName),
            NormalizeTextKey(itemName),
            NormalizeTextKey(manufacturer),
            NormalizeTextKey(machineNumber),
            monthlyFee.ToString("0.################", CultureInfo.InvariantCulture),
            contractMonths.ToString(CultureInfo.InvariantCulture),
            NormalizeProfileKeyPart(assetStatus));
    }

    public static string BuildRentalBillingProfileDuplicateKey(
        string? managementCompanyCode,
        string? responsibleOfficeCode,
        Guid? customerId,
        string? businessNumber,
        string? customerName,
        string? billingType,
        string? billingAdvanceMode,
        int billingDay,
        int billingCycleMonths,
        string? billingMethod)
        => BuildProfileKey(
            string.IsNullOrWhiteSpace(responsibleOfficeCode) ? managementCompanyCode : responsibleOfficeCode,
            customerId,
            businessNumber,
            customerName,
            billingType,
            billingAdvanceMode,
            billingDay,
            billingCycleMonths,
            billingMethod);

    public static string RemapBillingTemplateIncludedAssetIds(
        string? templateJson,
        IReadOnlyDictionary<Guid, Guid>? assetIdReplacements)
        => MergeBillingTemplateJson(templateJson, null, assetIdReplacements);

    public static string MergeBillingTemplateJson(
        string? primaryJson,
        string? secondaryJson,
        IReadOnlyDictionary<Guid, Guid>? assetIdReplacements = null)
    {
        var merged = new List<JsonObject>();
        var order = new List<string>();
        var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var source in new[] { primaryJson, secondaryJson })
        {
            foreach (var node in ParseObjectArray(source))
            {
                NormalizeIncludedAssetIds(node, assetIdReplacements);
                var key = GetBillingTemplateDedupKey(node);
                if (byKey.TryGetValue(key, out var existing))
                {
                    MergeObjectValues(existing, node);
                    MergeIncludedAssetIds(existing, node);
                    continue;
                }

                var clone = (JsonObject)node.DeepClone();
                byKey[key] = clone;
                order.Add(key);
            }
        }

        foreach (var key in order)
            merged.Add(byKey[key]);

        return JsonSerializer.Serialize(merged);
    }

    public static string MergeBillingRunsJson(string? primaryJson, string? secondaryJson)
    {
        var merged = new List<JsonObject>();
        var order = new List<string>();
        var byKey = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

        foreach (var source in new[] { primaryJson, secondaryJson })
        {
            foreach (var node in ParseObjectArray(source))
            {
                var hasCanonicalCoreProperties = CanonicalizeBillingRunCoreProperties(node);
                var baseKey = hasCanonicalCoreProperties
                    ? GetBillingRunDedupKey(node)
                    : $"INVALID:{NormalizeTrimmed(node.ToJsonString())}";
                var key = baseKey;
                if (baseKey.StartsWith("RUN:", StringComparison.Ordinal) &&
                    byKey.TryGetValue(baseKey, out var primaryIdentity) &&
                    HasConflictingRunKeys(primaryIdentity, node))
                {
                    key = $"{baseKey}|RUNKEY-CONFLICT:{ReadNormalizedRunKey(node)}";
                }

                if (byKey.TryGetValue(key, out var existing))
                {
                    EnrichRunKey(existing, node);
                    var existingTombstone = ReadBillingRunTombstone(existing);
                    var incomingTombstone = ReadBillingRunTombstone(node);
                    MergeObjectValues(
                        existing,
                        node,
                        propertyName =>
                            !IsBillingRunCoreProperty(propertyName) &&
                            !IsBillingRunTombstoneProperty(propertyName));
                    ApplyWinningBillingRunTombstone(existing, existingTombstone, incomingTombstone);
                    var existingItems = existing["Items"]?.ToJsonString() ?? "[]";
                    var incomingItems = node["Items"]?.ToJsonString() ?? "[]";
                    existing["Items"] = JsonNode.Parse(MergeBillingTemplateJson(existingItems, incomingItems));
                    CanonicalizeBillingRunCoreProperties(existing);
                    continue;
                }

                var clone = (JsonObject)node.DeepClone();
                if (clone["Items"] is JsonArray itemsArray)
                    clone["Items"] = JsonNode.Parse(MergeBillingTemplateJson(itemsArray.ToJsonString(), null));
                ApplyWinningBillingRunTombstone(
                    clone,
                    ReadBillingRunTombstone(node),
                    null);
                byKey[key] = clone;
                order.Add(key);
            }
        }

        foreach (var key in order)
            merged.Add(byKey[key]);

        return JsonSerializer.Serialize(merged);
    }

    private static BillingRunTombstone? ReadBillingRunTombstone(JsonObject node)
    {
        try
        {
            if (GetSinglePropertyValue(
                    node,
                    RentalBillingRunTombstonePolicy.IsTombstonedPropertyName) is not JsonValue isTombstonedValue ||
                !isTombstonedValue.TryGetValue<bool>(out var isTombstoned) ||
                !isTombstoned ||
                GetSinglePropertyValue(
                    node,
                    RentalBillingRunTombstonePolicy.TombstonedAtUtcPropertyName) is not JsonValue tombstonedAtUtcValue ||
                !tombstonedAtUtcValue.TryGetValue<DateTime>(out var tombstonedAtUtc) ||
                tombstonedAtUtc.Kind != DateTimeKind.Utc ||
                GetSinglePropertyValue(
                    node,
                    RentalBillingRunTombstonePolicy.TombstonedByUsernamePropertyName) is not JsonValue usernameValue ||
                !usernameValue.TryGetValue<string>(out var username) ||
                string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            return new BillingRunTombstone(tombstonedAtUtc, username.Trim());
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyWinningBillingRunTombstone(
        JsonObject target,
        BillingRunTombstone? existing,
        BillingRunTombstone? incoming)
    {
        var winner = existing.HasValue && incoming.HasValue
            ? existing.Value.TombstonedAtUtc >= incoming.Value.TombstonedAtUtc
                ? existing
                : incoming
            : existing ?? incoming;
        if (!winner.HasValue)
            return;

        RemoveProperties(target, RentalBillingRunTombstonePolicy.IsTombstonedPropertyName);
        RemoveProperties(target, RentalBillingRunTombstonePolicy.TombstonedAtUtcPropertyName);
        RemoveProperties(target, RentalBillingRunTombstonePolicy.TombstonedByUsernamePropertyName);
        RemoveProperties(target, "Status");
        RemoveProperties(target, "BilledAmount");
        RemoveProperties(target, "SettledAmount");
        RemoveProperties(target, "SettlementStatus");
        RemoveProperties(target, "SettledDate");
        target[RentalBillingRunTombstonePolicy.IsTombstonedPropertyName] = true;
        target[RentalBillingRunTombstonePolicy.TombstonedAtUtcPropertyName] = winner.Value.TombstonedAtUtc;
        target[RentalBillingRunTombstonePolicy.TombstonedByUsernamePropertyName] = winner.Value.TombstonedByUsername;
        target["Status"] = "취소";
        target["BilledAmount"] = 0m;
        target["SettledAmount"] = 0m;
        target["SettlementStatus"] = "미입금";
        target["SettledDate"] = null;
    }

    private static bool IsBillingRunTombstoneProperty(string propertyName)
        => string.Equals(
               propertyName,
               RentalBillingRunTombstonePolicy.IsTombstonedPropertyName,
               StringComparison.OrdinalIgnoreCase) ||
           string.Equals(
               propertyName,
               RentalBillingRunTombstonePolicy.TombstonedAtUtcPropertyName,
               StringComparison.OrdinalIgnoreCase) ||
           string.Equals(
               propertyName,
               RentalBillingRunTombstonePolicy.TombstonedByUsernamePropertyName,
               StringComparison.OrdinalIgnoreCase);

    private static bool IsBillingRunCoreProperty(string propertyName)
        => string.Equals(
               propertyName,
               RentalBillingRunTombstonePolicy.RunIdPropertyName,
               StringComparison.OrdinalIgnoreCase) ||
           string.Equals(propertyName, "RunKey", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(propertyName, "Items", StringComparison.OrdinalIgnoreCase);

    private static bool HasConflictingRunKeys(JsonObject existing, JsonObject incoming)
    {
        var existingRunKey = ReadNormalizedRunKey(existing);
        var incomingRunKey = ReadNormalizedRunKey(incoming);
        return !string.IsNullOrWhiteSpace(existingRunKey) &&
               !string.IsNullOrWhiteSpace(incomingRunKey) &&
               !string.Equals(existingRunKey, incomingRunKey, StringComparison.Ordinal);
    }

    private static void EnrichRunKey(JsonObject target, JsonObject source)
    {
        if (!string.IsNullOrWhiteSpace(ReadNormalizedRunKey(target)))
            return;

        if (GetSinglePropertyValue(source, "RunKey") is not JsonValue sourceRunKey ||
            !sourceRunKey.TryGetValue<string>(out var sourceRunKeyText) ||
            string.IsNullOrWhiteSpace(NormalizeProfileKeyPart(sourceRunKeyText)))
        {
            return;
        }

        RemoveProperties(target, "RunKey");
        target["RunKey"] = sourceRunKeyText;
    }

    private static string ReadNormalizedRunKey(JsonObject node)
        => GetSinglePropertyValue(node, "RunKey") is JsonValue runKeyValue &&
           runKeyValue.TryGetValue<string>(out var runKey)
            ? NormalizeProfileKeyPart(runKey)
            : string.Empty;

    private static bool CanonicalizeBillingRunCoreProperties(JsonObject node)
    {
        var runIdIsCanonical = CanonicalizeRunIdProperty(node);
        var runKeyIsCanonical = CanonicalizeRunKeyProperty(node);
        var itemsAreCanonical = CanonicalizeItemsProperty(node);
        return runIdIsCanonical && runKeyIsCanonical && itemsAreCanonical;
    }

    private static bool CanonicalizeRunIdProperty(JsonObject node)
    {
        var matches = GetPropertyMatches(node, RentalBillingRunTombstonePolicy.RunIdPropertyName);
        if (matches.Count == 0)
            return true;

        var runIds = new List<Guid>();
        foreach (var match in matches)
        {
            if (match.Value is not JsonValue value ||
                !value.TryGetValue<Guid>(out var runId) ||
                runId == Guid.Empty)
            {
                return false;
            }

            runIds.Add(runId);
        }

        var distinctRunIds = runIds.Distinct().ToList();
        if (distinctRunIds.Count != 1)
            return false;

        RemoveProperties(node, RentalBillingRunTombstonePolicy.RunIdPropertyName);
        node[RentalBillingRunTombstonePolicy.RunIdPropertyName] = distinctRunIds[0];
        return true;
    }

    private static bool CanonicalizeRunKeyProperty(JsonObject node)
    {
        var matches = GetPropertyMatches(node, "RunKey");
        if (matches.Count == 0)
            return true;

        var values = new List<string>();
        foreach (var match in matches)
        {
            if (match.Value is null)
                continue;
            if (match.Value is not JsonValue value || !value.TryGetValue<string>(out var runKey))
                return false;

            values.Add(runKey ?? string.Empty);
        }

        if (values.Count == 0)
        {
            RemoveProperties(node, "RunKey");
            return true;
        }

        if (values.Select(NormalizeProfileKeyPart).Distinct(StringComparer.Ordinal).Count() != 1)
            return false;

        var canonicalValue = matches
            .Where(match =>
                string.Equals(match.Key, "RunKey", StringComparison.Ordinal) &&
                match.Value is not null)
            .Select(match => match.Value?.GetValue<string>() ?? string.Empty)
            .FirstOrDefault() ?? values[0];
        RemoveProperties(node, "RunKey");
        node["RunKey"] = canonicalValue;
        return true;
    }

    private static bool CanonicalizeItemsProperty(JsonObject node)
    {
        var matches = GetPropertyMatches(node, "Items");
        if (matches.Count == 0)
            return true;
        if (matches.Any(match => match.Value is not JsonArray))
            return false;

        var mergedItemsJson = "[]";
        foreach (var match in matches)
        {
            mergedItemsJson = MergeBillingTemplateJson(
                mergedItemsJson,
                match.Value!.ToJsonString());
        }

        RemoveProperties(node, "Items");
        node["Items"] = JsonNode.Parse(mergedItemsJson);
        return true;
    }

    private static List<KeyValuePair<string, JsonNode?>> GetPropertyMatches(
        JsonObject node,
        string propertyName)
        => node
            .Where(property => string.Equals(
                property.Key,
                propertyName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static JsonNode? GetSinglePropertyValue(JsonObject node, string propertyName)
    {
        var matches = GetPropertyMatches(node, propertyName);
        return matches.Count == 1 ? matches[0].Value : null;
    }

    private static void RemoveProperties(JsonObject node, string propertyName)
    {
        var keys = node
            .Select(property => property.Key)
            .Where(key => string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var key in keys)
            node.Remove(key);
    }

    private readonly record struct BillingRunTombstone(
        DateTime TombstonedAtUtc,
        string TombstonedByUsername);

    private static string ExtractIdentifier(string? notes, Regex regex)
    {
        if (string.IsNullOrWhiteSpace(notes))
            return string.Empty;

        var matches = regex.Matches(notes);
        for (var index = matches.Count - 1; index >= 0; index--)
        {
            var value = matches[index].Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    private static List<JsonObject> ParseObjectArray(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            var node = JsonNode.Parse(json);
            if (node is not JsonArray array)
                return [];

            return array
                .OfType<JsonObject>()
                .Select(current => (JsonObject)current.DeepClone())
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string GetBillingTemplateDedupKey(JsonObject node)
    {
        if (node["ItemId"] is JsonValue itemIdValue && itemIdValue.TryGetValue<Guid>(out var itemId) && itemId != Guid.Empty)
            return $"ITEM:{itemId:D}";

        return string.Join('|',
            NormalizeTextKey(node["DisplayItemName"]?.GetValue<string>()),
            NormalizeProfileKeyPart(node["BillingLineMode"]?.GetValue<string>()),
            NormalizeTrimmed(node["Quantity"]?.ToJsonString()),
            NormalizeTrimmed(node["UnitPrice"]?.ToJsonString()),
            NormalizeTrimmed(node["Amount"]?.ToJsonString()),
            NormalizeTextKey(node["Note"]?.GetValue<string>()));
    }

    private static string GetBillingRunDedupKey(JsonObject node)
    {
        if (GetSinglePropertyValue(node, RentalBillingRunTombstonePolicy.RunIdPropertyName) is JsonValue runIdValue &&
            runIdValue.TryGetValue<Guid>(out var runId) &&
            runId != Guid.Empty)
        {
            return $"RUN:{runId:D}";
        }

        var runKey = NormalizeProfileKeyPart(GetSinglePropertyValue(node, "RunKey")?.GetValue<string>());
        if (!string.IsNullOrWhiteSpace(runKey))
            return $"RUNKEY:{runKey}";

        return $"FALLBACK:{NormalizeTrimmed(node.ToJsonString())}";
    }

    private static void NormalizeIncludedAssetIds(JsonObject node, IReadOnlyDictionary<Guid, Guid>? assetIdReplacements)
    {
        var ids = ExtractGuidArray(node["IncludedAssetIds"], assetIdReplacements);
        node["IncludedAssetIds"] = new JsonArray(ids.Select(id => JsonValue.Create(id)).ToArray());
    }

    private static void MergeIncludedAssetIds(JsonObject target, JsonObject source)
    {
        var merged = ExtractGuidArray(target["IncludedAssetIds"], null)
            .Concat(ExtractGuidArray(source["IncludedAssetIds"], null))
            .Where(id => id != Guid.Empty)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        target["IncludedAssetIds"] = new JsonArray(merged.Select(id => JsonValue.Create(id)).ToArray());
    }

    private static List<Guid> ExtractGuidArray(JsonNode? node, IReadOnlyDictionary<Guid, Guid>? assetIdReplacements)
    {
        if (node is not JsonArray array)
            return [];

        var result = new List<Guid>();
        foreach (var current in array)
        {
            if (current is not JsonValue value || !value.TryGetValue<Guid>(out var id) || id == Guid.Empty)
                continue;

            if (assetIdReplacements is not null && assetIdReplacements.TryGetValue(id, out var replacement) && replacement != Guid.Empty)
                id = replacement;

            result.Add(id);
        }

        return result.Distinct().OrderBy(id => id).ToList();
    }

    private static void MergeObjectValues(JsonObject target, JsonObject source, Func<string, bool>? propertyFilter = null)
    {
        foreach (var property in source)
        {
            if (propertyFilter is not null && !propertyFilter(property.Key))
                continue;

            var incoming = property.Value;
            if (incoming is null)
                continue;

            if (!target.TryGetPropertyValue(property.Key, out var existing) || IsMeaningless(existing))
            {
                target[property.Key] = incoming.DeepClone();
                continue;
            }

            if (incoming is JsonValue incomingValue && existing is JsonValue existingValue)
            {
                if (TryGetString(existingValue, out var existingText) && TryGetString(incomingValue, out var incomingText))
                {
                    if (string.IsNullOrWhiteSpace(existingText) && !string.IsNullOrWhiteSpace(incomingText))
                        target[property.Key] = incoming.DeepClone();
                    else if (!string.IsNullOrWhiteSpace(incomingText) && incomingText.Length > existingText.Length)
                        target[property.Key] = incoming.DeepClone();
                }
            }
        }
    }

    private static bool TryGetString(JsonValue value, out string text)
    {
        if (value.TryGetValue<string>(out text!))
            return true;

        text = value.ToJsonString().Trim('"');
        return true;
    }

    private static bool IsMeaningless(JsonNode? node)
    {
        if (node is null)
            return true;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text))
                return string.IsNullOrWhiteSpace(text);

            return false;
        }

        if (node is JsonArray array)
            return array.Count == 0;

        return false;
    }
}
