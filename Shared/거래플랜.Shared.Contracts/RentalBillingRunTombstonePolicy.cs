using System.Globalization;
using System.Text.Json;

namespace 거래플랜.Shared.Contracts;

public enum RentalBillingRunLookupStatus
{
    NotFound,
    Active,
    Tombstoned,
    InvalidJson,
    InvalidMarker
}

public readonly record struct RentalBillingRunLookupResult(
    RentalBillingRunLookupStatus Status,
    DateTime? TombstonedAtUtc = null,
    string TombstonedByUsername = "",
    string Error = "")
{
    public bool IsValid => Status is not RentalBillingRunLookupStatus.InvalidJson
        and not RentalBillingRunLookupStatus.InvalidMarker;

    public bool IsFound => Status is RentalBillingRunLookupStatus.Active
        or RentalBillingRunLookupStatus.Tombstoned;

    public bool IsTombstoned => Status == RentalBillingRunLookupStatus.Tombstoned;
}

public static class RentalBillingRunTombstonePolicy
{
    public const string RunIdPropertyName = "RunId";
    public const string IsTombstonedPropertyName = "IsTombstoned";
    public const string TombstonedAtUtcPropertyName = "TombstonedAtUtc";
    public const string TombstonedByUsernamePropertyName = "TombstonedByUsername";
    private static readonly string[] OptionalCorePropertyNames =
    [
        "RunKey",
        "ScheduledDate",
        "PeriodStartDate",
        "PeriodEndDate",
        "CycleMonths",
        "PeriodLabel",
        "Status",
        "BilledAmount",
        "SettledAmount",
        "SettlementStatus",
        "SettledDate",
        "Note",
        "Items"
    ];
    private static readonly string[] OptionalStringCorePropertyNames =
    [
        "PeriodLabel",
        "Note"
    ];
    private static readonly HashSet<string> AllowedRunStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "\uC608\uC815",
        "\uCCAD\uAD6C\uC911",
        "\uBD80\uBD84\uC785\uAE08",
        "\uC644\uB8CC",
        "\uBCF4\uB958",
        "\uCDE8\uC18C"
    };
    private static readonly HashSet<string> AllowedRunSettlementStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "\uBBF8\uC785\uAE08",
        "\uD655\uC778\uB300\uAE30",
        "\uBD80\uBD84\uC785\uAE08",
        "\uC785\uAE08\uD655\uC778",
        "\uCE74\uB4DC\uACB0\uC81C\uB300\uAE30",
        "\uCE74\uB4DC\uC2B9\uC778\uC644\uB8CC",
        "CMS\uB300\uAE30",
        "CMS\uC2E4\uD328",
        "\uD658\uBD88"
    };

    public static bool IsValidRunStatus(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var normalized = value?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized) || AllowedRunStatuses.Contains(normalized);
    }

    public static bool IsValidRunSettlementStatus(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(normalized) &&
               AllowedRunSettlementStatuses.Contains(normalized);
    }

    public static RentalBillingRunLookupResult Validate(string? billingRunsJson)
        => LookupCore(
            billingRunsJson,
            Guid.Empty,
            allowRepairableLegacyValuesForFinancialRecalculation: false,
            rejectDuplicateActiveRunIds: false);

    public static RentalBillingRunLookupResult ValidateForServerMutation(
        string? billingRunsJson)
        => LookupCore(
            billingRunsJson,
            Guid.Empty,
            allowRepairableLegacyValuesForFinancialRecalculation: false,
            rejectDuplicateActiveRunIds: true);

    public static RentalBillingRunLookupResult ValidateForFinancialRecalculation(
        string? billingRunsJson)
        => LookupCore(
            billingRunsJson,
            Guid.Empty,
            allowRepairableLegacyValuesForFinancialRecalculation: true,
            rejectDuplicateActiveRunIds: true);

    public static RentalBillingRunLookupResult Lookup(string? billingRunsJson, Guid runId)
        => LookupCore(
            billingRunsJson,
            runId,
            allowRepairableLegacyValuesForFinancialRecalculation: false,
            rejectDuplicateActiveRunIds: false);

    public static RentalBillingRunLookupResult LookupForServerMutation(
        string? billingRunsJson,
        Guid runId)
        => LookupCore(
            billingRunsJson,
            runId,
            allowRepairableLegacyValuesForFinancialRecalculation: false,
            rejectDuplicateActiveRunIds: true);

    public static RentalBillingRunLookupResult LookupForFinancialRecalculation(
        string? billingRunsJson,
        Guid runId)
        => LookupCore(
            billingRunsJson,
            runId,
            allowRepairableLegacyValuesForFinancialRecalculation: true,
            rejectDuplicateActiveRunIds: true);

    private static RentalBillingRunLookupResult LookupCore(
        string? billingRunsJson,
        Guid runId,
        bool allowRepairableLegacyValuesForFinancialRecalculation,
        bool rejectDuplicateActiveRunIds)
    {
        if (string.IsNullOrWhiteSpace(billingRunsJson))
        {
            return new RentalBillingRunLookupResult(
                RentalBillingRunLookupStatus.NotFound);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(billingRunsJson);
        }
        catch (JsonException exception)
        {
            return new RentalBillingRunLookupResult(
                RentalBillingRunLookupStatus.InvalidJson,
                Error: exception.Message);
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new RentalBillingRunLookupResult(
                    RentalBillingRunLookupStatus.InvalidJson,
                    Error: "Rental billing runs JSON must be an array.");
            }

            var foundActive = false;
            RentalBillingRunLookupResult? foundTombstone = null;
            var runIdToRunKey = new Dictionary<Guid, string>();
            var runKeyToRunId = new Dictionary<string, Guid>(StringComparer.Ordinal);
            var activeRunIds = new HashSet<Guid>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    return new RentalBillingRunLookupResult(
                        RentalBillingRunLookupStatus.InvalidJson,
                        Error: "Each rental billing run JSON array element must be an object.");
                }

                var marker = ReadMarker(element);
                if (!marker.IsValid)
                    return marker;

                var runIdProperty = FindProperty(element, RunIdPropertyName);
                if (runIdProperty.IsDuplicate)
                {
                    return new RentalBillingRunLookupResult(
                        RentalBillingRunLookupStatus.InvalidJson,
                        Error: "Rental billing run JSON contains duplicate RunId properties with conflicting casing.");
                }

                if (!runIdProperty.IsFound)
                {
                    if (marker.IsTombstoned)
                    {
                        return InvalidMarker(
                            "A tombstoned rental billing run requires a non-empty RunId.");
                    }

                    if (ValidateOptionalCoreProperties(
                            element,
                            allowRepairableLegacyValuesForFinancialRecalculation) is { } corePropertyError)
                        return corePropertyError;
                    continue;
                }
                if (runIdProperty.Value.ValueKind != JsonValueKind.String ||
                    !runIdProperty.Value.TryGetGuid(out var candidateRunId))
                {
                    return new RentalBillingRunLookupResult(
                        RentalBillingRunLookupStatus.InvalidJson,
                        Error: "Rental billing run RunId must be a valid GUID string.");
                }

                if (candidateRunId == Guid.Empty)
                {
                    if (marker.IsTombstoned)
                    {
                        return InvalidMarker(
                            "A tombstoned rental billing run requires a non-empty RunId.");
                    }

                    if (ValidateOptionalCoreProperties(
                            element,
                            allowRepairableLegacyValuesForFinancialRecalculation) is { } emptyRunIdCorePropertyError)
                        return emptyRunIdCorePropertyError;
                    continue;
                }

                if (ValidateOptionalCoreProperties(
                        element,
                        allowRepairableLegacyValuesForFinancialRecalculation) is { } optionalCorePropertyError)
                    return optionalCorePropertyError;

                var normalizedRunKey = ReadNormalizedRunKey(element);
                if (ValidateIdentityGraph(
                        candidateRunId,
                        normalizedRunKey,
                        runIdToRunKey,
                        runKeyToRunId) is { } identityGraphError)
                {
                    return identityGraphError;
                }

                if (rejectDuplicateActiveRunIds &&
                    !marker.IsTombstoned &&
                    !activeRunIds.Add(candidateRunId))
                {
                    return new RentalBillingRunLookupResult(
                        RentalBillingRunLookupStatus.InvalidJson,
                        Error: "Rental billing run identity graph contains duplicate active RunId rows.");
                }

                if (runId == Guid.Empty || candidateRunId != runId)
                    continue;

                if (marker.IsTombstoned)
                    foundTombstone ??= marker;
                else
                    foundActive = true;
            }

            if (foundTombstone.HasValue)
                return foundTombstone.Value;

            return new RentalBillingRunLookupResult(
                foundActive
                    ? RentalBillingRunLookupStatus.Active
                    : RentalBillingRunLookupStatus.NotFound);
        }
    }

    private static RentalBillingRunLookupResult ReadMarker(JsonElement element)
    {
        var isTombstonedProperty = FindProperty(element, IsTombstonedPropertyName);
        var tombstonedAtUtcProperty = FindProperty(element, TombstonedAtUtcPropertyName);
        var tombstonedByUsernameProperty = FindProperty(element, TombstonedByUsernamePropertyName);

        if (isTombstonedProperty.IsDuplicate ||
            tombstonedAtUtcProperty.IsDuplicate ||
            tombstonedByUsernameProperty.IsDuplicate)
        {
            return InvalidMarker(
                "Rental billing run tombstone marker contains duplicate properties with conflicting casing.");
        }

        if (!isTombstonedProperty.IsFound &&
            !tombstonedAtUtcProperty.IsFound &&
            !tombstonedByUsernameProperty.IsFound)
        {
            return new RentalBillingRunLookupResult(RentalBillingRunLookupStatus.Active);
        }

        if (!isTombstonedProperty.IsFound ||
            !tombstonedAtUtcProperty.IsFound ||
            !tombstonedByUsernameProperty.IsFound)
        {
            return InvalidMarker("Rental billing run tombstone marker fields are incomplete.");
        }

        var isTombstonedElement = isTombstonedProperty.Value;
        var tombstonedAtUtcElement = tombstonedAtUtcProperty.Value;
        var tombstonedByUsernameElement = tombstonedByUsernameProperty.Value;
        if (
            isTombstonedElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            tombstonedByUsernameElement.ValueKind != JsonValueKind.String)
        {
            return InvalidMarker("Rental billing run tombstone marker fields are incomplete or have invalid types.");
        }

        var isTombstoned = isTombstonedElement.GetBoolean();
        var tombstonedByUsername = tombstonedByUsernameElement.GetString()?.Trim() ?? string.Empty;
        DateTime? tombstonedAtUtc = null;
        if (tombstonedAtUtcElement.ValueKind != JsonValueKind.Null)
        {
            if (tombstonedAtUtcElement.ValueKind != JsonValueKind.String ||
                !tombstonedAtUtcElement.TryGetDateTime(out var parsedAtUtc) ||
                parsedAtUtc.Kind != DateTimeKind.Utc)
            {
                return InvalidMarker("TombstonedAtUtc must be null or a UTC DateTime.");
            }

            tombstonedAtUtc = parsedAtUtc;
        }

        if (isTombstoned)
        {
            if (!tombstonedAtUtc.HasValue || string.IsNullOrWhiteSpace(tombstonedByUsername))
            {
                return InvalidMarker("A tombstoned rental billing run requires UTC timestamp and username metadata.");
            }

            return new RentalBillingRunLookupResult(
                RentalBillingRunLookupStatus.Tombstoned,
                tombstonedAtUtc,
                tombstonedByUsername);
        }

        if (tombstonedAtUtc.HasValue || !string.IsNullOrWhiteSpace(tombstonedByUsername))
        {
            return InvalidMarker("An active rental billing run cannot retain tombstone metadata.");
        }

        return new RentalBillingRunLookupResult(RentalBillingRunLookupStatus.Active);
    }

    private static JsonPropertyLookup FindProperty(JsonElement element, string propertyName)
    {
        var count = 0;
        var value = default(JsonElement);
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            count++;
            if (count == 1)
                value = property.Value;
        }

        return new JsonPropertyLookup(count, value);
    }

    private static RentalBillingRunLookupResult? ValidateOptionalCoreProperties(
        JsonElement element,
        bool allowRepairableLegacyValuesForFinancialRecalculation)
    {
        var runKeyProperty = FindProperty(element, "RunKey");
        var itemsProperty = FindProperty(element, "Items");
        if (OptionalCorePropertyNames.Any(propertyName => FindProperty(element, propertyName).IsDuplicate))
        {
            return new RentalBillingRunLookupResult(
                RentalBillingRunLookupStatus.InvalidJson,
                Error: "Rental billing run JSON contains duplicate core properties with conflicting casing.");
        }

        if (runKeyProperty.IsFound &&
            runKeyProperty.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
        {
            return new RentalBillingRunLookupResult(
                RentalBillingRunLookupStatus.InvalidJson,
                Error: "Rental billing run RunKey must be a string.");
        }

        if (itemsProperty.IsFound && itemsProperty.Value.ValueKind != JsonValueKind.Array)
        {
            return new RentalBillingRunLookupResult(
                RentalBillingRunLookupStatus.InvalidJson,
                Error: "Rental billing run Items must be an array.");
        }

        foreach (var propertyName in OptionalStringCorePropertyNames)
        {
            var property = FindProperty(element, propertyName);
            if (property.IsFound &&
                property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
            {
                return InvalidCoreProperty(propertyName, "a string or null");
            }
        }

        var statusProperty = FindProperty(element, "Status");
        if (statusProperty.IsFound &&
            (!allowRepairableLegacyValuesForFinancialRecalculation
                ? statusProperty.Value.ValueKind != JsonValueKind.String ||
                  !IsValidRunStatus(statusProperty.Value.GetString())
                : statusProperty.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
        {
            return InvalidCoreProperty(
                "Status",
                allowRepairableLegacyValuesForFinancialRecalculation
                    ? "a legacy string or null during financial recalculation"
                    : "blank or one of the supported rental billing run statuses");
        }

        var settlementStatusProperty = FindProperty(element, "SettlementStatus");
        if (settlementStatusProperty.IsFound &&
            (!allowRepairableLegacyValuesForFinancialRecalculation
                ? settlementStatusProperty.Value.ValueKind != JsonValueKind.String ||
                  !IsValidRunSettlementStatus(settlementStatusProperty.Value.GetString())
                : settlementStatusProperty.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
        {
            return InvalidCoreProperty(
                "SettlementStatus",
                allowRepairableLegacyValuesForFinancialRecalculation
                    ? "a legacy string or null during financial recalculation"
                    : "one of the supported non-blank rental settlement statuses");
        }

        foreach (var propertyName in new[] { "ScheduledDate", "PeriodStartDate", "PeriodEndDate" })
        {
            var property = FindProperty(element, propertyName);
            if (property.IsFound && !TryReadDateOnly(property.Value, allowNull: false, out _))
                return InvalidCoreProperty(propertyName, "a yyyy-MM-dd date string");
        }

        var settledDateProperty = FindProperty(element, "SettledDate");
        if (settledDateProperty.IsFound &&
            !TryReadDateOnly(settledDateProperty.Value, allowNull: true, out _))
        {
            return InvalidCoreProperty("SettledDate", "a yyyy-MM-dd date string or null");
        }

        var periodStartDateProperty = FindProperty(element, "PeriodStartDate");
        var periodEndDateProperty = FindProperty(element, "PeriodEndDate");
        if (!allowRepairableLegacyValuesForFinancialRecalculation &&
            periodStartDateProperty.IsFound &&
            periodEndDateProperty.IsFound &&
            TryReadDateOnly(periodStartDateProperty.Value, allowNull: false, out var periodStartDate) &&
            TryReadDateOnly(periodEndDateProperty.Value, allowNull: false, out var periodEndDate) &&
            periodStartDate > periodEndDate)
        {
            return new RentalBillingRunLookupResult(
                RentalBillingRunLookupStatus.InvalidJson,
                Error: "Rental billing run period start date cannot be after its end date.");
        }

        foreach (var propertyName in new[] { "BilledAmount", "SettledAmount" })
        {
            var property = FindProperty(element, propertyName);
            if (property.IsFound &&
                (property.Value.ValueKind != JsonValueKind.Number ||
                 !property.Value.TryGetDecimal(out var amount) ||
                 amount < 0m))
            {
                return InvalidCoreProperty(propertyName, "a non-negative decimal number");
            }
        }

        var cycleMonthsProperty = FindProperty(element, "CycleMonths");
        if (cycleMonthsProperty.IsFound &&
            (cycleMonthsProperty.Value.ValueKind != JsonValueKind.Number ||
             !cycleMonthsProperty.Value.TryGetInt32(out var cycleMonths) ||
             cycleMonths > 1200 ||
             (!allowRepairableLegacyValuesForFinancialRecalculation && cycleMonths < 1)))
        {
            return InvalidCoreProperty(
                "CycleMonths",
                allowRepairableLegacyValuesForFinancialRecalculation
                    ? "a legacy integer during financial recalculation"
                    : "an integer from 1 through 1200");
        }

        return null;
    }

    private static RentalBillingRunLookupResult InvalidCoreProperty(
        string propertyName,
        string expectedType)
        => new(
            RentalBillingRunLookupStatus.InvalidJson,
            Error: $"Rental billing run {propertyName} must be {expectedType}.");

    private static bool TryReadDateOnly(
        JsonElement value,
        bool allowNull,
        out DateOnly date)
    {
        date = default;
        if (value.ValueKind == JsonValueKind.Null)
            return allowNull;
        return value.ValueKind == JsonValueKind.String &&
               DateOnly.TryParseExact(
                   value.GetString(),
                   "yyyy-MM-dd",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.None,
                   out date);
    }

    private static string ReadNormalizedRunKey(JsonElement element)
    {
        var runKeyProperty = FindProperty(element, "RunKey");
        return runKeyProperty.IsFound && runKeyProperty.Value.ValueKind == JsonValueKind.String
            ? RentalDuplicateNormalizer.NormalizeProfileKeyPart(runKeyProperty.Value.GetString())
            : string.Empty;
    }

    private static RentalBillingRunLookupResult? ValidateIdentityGraph(
        Guid runId,
        string normalizedRunKey,
        IDictionary<Guid, string> runIdToRunKey,
        IDictionary<string, Guid> runKeyToRunId)
    {
        if (runIdToRunKey.TryGetValue(runId, out var existingRunKey))
        {
            if (!string.IsNullOrWhiteSpace(existingRunKey) &&
                !string.IsNullOrWhiteSpace(normalizedRunKey) &&
                !string.Equals(existingRunKey, normalizedRunKey, StringComparison.Ordinal))
            {
                return new RentalBillingRunLookupResult(
                    RentalBillingRunLookupStatus.InvalidJson,
                    Error: "Rental billing run identity graph contains duplicate RunId or RunKey values: one RunId maps to conflicting RunKey values.");
            }

            if (string.IsNullOrWhiteSpace(existingRunKey) &&
                !string.IsNullOrWhiteSpace(normalizedRunKey))
            {
                runIdToRunKey[runId] = normalizedRunKey;
            }
        }
        else
        {
            runIdToRunKey[runId] = normalizedRunKey;
        }

        if (string.IsNullOrWhiteSpace(normalizedRunKey))
            return null;

        if (runKeyToRunId.TryGetValue(normalizedRunKey, out var existingRunId) &&
            existingRunId != runId)
        {
            return new RentalBillingRunLookupResult(
                RentalBillingRunLookupStatus.InvalidJson,
                Error: "Rental billing run identity graph contains duplicate RunId or RunKey values: one RunKey maps to conflicting RunId values.");
        }

        runKeyToRunId[normalizedRunKey] = runId;
        return null;
    }

    private static RentalBillingRunLookupResult InvalidMarker(string error)
        => new(RentalBillingRunLookupStatus.InvalidMarker, Error: error);

    private readonly record struct JsonPropertyLookup(int Count, JsonElement Value)
    {
        public bool IsFound => Count == 1;
        public bool IsDuplicate => Count > 1;
    }
}
