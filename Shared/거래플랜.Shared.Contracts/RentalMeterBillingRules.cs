using System.Globalization;
using System.Text.Json;

namespace 거래플랜.Shared.Contracts;

public static class RentalMeterPolicyModes
{
    public const string Unconfigured = "미설정";
    public const string Unlimited = "무제한";
    public const string Numeric = "수량지정";

    public static string Normalize(string? value, int? includedPages = null)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (string.Equals(trimmed, Unlimited, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "UNLIMITED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "면제", StringComparison.OrdinalIgnoreCase))
        {
            return Unlimited;
        }

        if (string.Equals(trimmed, Numeric, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "NUMERIC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "지정", StringComparison.OrdinalIgnoreCase) ||
            includedPages.HasValue)
        {
            return Numeric;
        }

        return Unconfigured;
    }
}

public sealed class RentalMeterReadingRecord
{
    public string BillingYearMonth { get; set; } = string.Empty;
    public DateOnly ReadingDate { get; set; }
    public long BlackMeter { get; set; }
    public long ColorMeter { get; set; }
    public bool IsFinalized { get; set; }
    public bool IsOpeningBaseline { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public string EvidenceReference { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed record RentalMeterAssetBillingInput(
    Guid AssetId,
    string ManagementNumber,
    string ItemName,
    bool MeterBillingEnabled,
    string BlackIncludedMode,
    int? BlackIncludedPages,
    decimal? BlackOverageUnitPrice,
    string ColorIncludedMode,
    int? ColorIncludedPages,
    decimal? ColorOverageUnitPrice,
    string MeterReadingsJson);

public sealed record RentalMeterChargeLine(
    string ColorMode,
    long UsagePages,
    long IncludedPages,
    long OveragePages,
    decimal UnitPrice,
    decimal Amount,
    IReadOnlyList<Guid> AssetIds,
    string EvidenceSummary);

public sealed class RentalMeterBillingResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public IReadOnlyList<RentalMeterChargeLine> Lines { get; init; } = [];
    public decimal TotalAmount => Lines.Sum(line => line.Amount);

    public static RentalMeterBillingResult Ok(IReadOnlyList<RentalMeterChargeLine> lines)
        => new() { Success = true, Lines = lines };

    public static RentalMeterBillingResult Review(string message)
        => new() { Success = false, Message = message };
}

public static class RentalMeterBillingRules
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string SerializeReadings(IEnumerable<RentalMeterReadingRecord>? readings)
        => JsonSerializer.Serialize(
            (readings ?? []).OrderBy(reading => reading.BillingYearMonth, StringComparer.Ordinal)
                .ThenBy(reading => reading.ReadingDate)
                .ThenBy(reading => reading.RecordedAtUtc)
                .ToList(),
            JsonOptions);

    public static IReadOnlyList<RentalMeterReadingRecord> ParseReadings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return (JsonSerializer.Deserialize<List<RentalMeterReadingRecord>>(json, JsonOptions) ?? [])
                .Where(reading => TryNormalizeBillingYearMonth(reading.BillingYearMonth, out _))
                .OrderBy(reading => reading.BillingYearMonth, StringComparer.Ordinal)
                .ThenBy(reading => reading.ReadingDate)
                .ThenBy(reading => reading.RecordedAtUtc)
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static RentalMeterReadingRecord? SelectOpeningBaseline(
        IEnumerable<RentalMeterReadingRecord>? historicalReadings)
    {
        var selected = (historicalReadings ?? [])
            .Where(reading => reading.IsFinalized)
            .Where(reading => TryNormalizeBillingYearMonth(reading.BillingYearMonth, out _))
            .OrderByDescending(reading => reading.BillingYearMonth, StringComparer.Ordinal)
            .ThenByDescending(reading => reading.ReadingDate)
            .ThenByDescending(reading => reading.RecordedAtUtc)
            .FirstOrDefault();
        if (selected is null)
            return null;

        return new RentalMeterReadingRecord
        {
            BillingYearMonth = NormalizeBillingYearMonth(selected.BillingYearMonth),
            ReadingDate = selected.ReadingDate,
            BlackMeter = selected.BlackMeter,
            ColorMeter = selected.ColorMeter,
            IsFinalized = true,
            IsOpeningBaseline = true,
            SourceSystem = selected.SourceSystem?.Trim() ?? string.Empty,
            EvidenceReference = selected.EvidenceReference?.Trim() ?? string.Empty,
            Note = selected.Note?.Trim() ?? string.Empty,
            RecordedAtUtc = NormalizeUtc(selected.RecordedAtUtc)
        };
    }

    public static RentalMeterBillingResult Calculate(
        string billingYearMonth,
        IEnumerable<RentalMeterAssetBillingInput>? assets,
        bool poolIncludedPages)
    {
        if (!TryNormalizeBillingYearMonth(billingYearMonth, out var normalizedYearMonth))
            return RentalMeterBillingResult.Review("검침 청구월이 올바르지 않습니다.");

        var enabledAssets = (assets ?? [])
            .Where(asset => asset.MeterBillingEnabled)
            .OrderBy(asset => asset.ManagementNumber, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(asset => asset.AssetId)
            .ToList();
        if (enabledAssets.Count == 0)
            return RentalMeterBillingResult.Ok([]);

        var usages = new List<AssetUsage>();
        foreach (var asset in enabledAssets)
        {
            var readings = ParseReadings(asset.MeterReadingsJson)
                .Where(reading => reading.IsFinalized)
                .ToList();
            var current = readings
                .Where(reading => string.Equals(
                    NormalizeBillingYearMonth(reading.BillingYearMonth),
                    normalizedYearMonth,
                    StringComparison.Ordinal))
                .OrderByDescending(reading => reading.ReadingDate)
                .ThenByDescending(reading => reading.RecordedAtUtc)
                .FirstOrDefault();
            var previous = readings
                .Where(reading => string.CompareOrdinal(
                    NormalizeBillingYearMonth(reading.BillingYearMonth),
                    normalizedYearMonth) < 0)
                .OrderByDescending(reading => reading.BillingYearMonth, StringComparer.Ordinal)
                .ThenByDescending(reading => reading.ReadingDate)
                .ThenByDescending(reading => reading.RecordedAtUtc)
                .FirstOrDefault();
            var assetLabel = BuildAssetLabel(asset);
            if (current is null)
                return RentalMeterBillingResult.Review($"{assetLabel}의 {normalizedYearMonth} 확정 검침값이 없습니다.");
            if (previous is null)
                return RentalMeterBillingResult.Review($"{assetLabel}의 이전 확정 검침값 또는 시작값이 없습니다.");
            if (current.BlackMeter < previous.BlackMeter || current.ColorMeter < previous.ColorMeter)
                return RentalMeterBillingResult.Review($"{assetLabel}의 현재 검침값이 이전 검침값보다 작습니다. 장비 교체 또는 검침 초기화를 확인하세요.");

            usages.Add(new AssetUsage(
                asset,
                current.BlackMeter - previous.BlackMeter,
                current.ColorMeter - previous.ColorMeter,
                $"{previous.BillingYearMonth} {previous.BlackMeter:N0}/{previous.ColorMeter:N0} → {normalizedYearMonth} {current.BlackMeter:N0}/{current.ColorMeter:N0}"));
        }

        return poolIncludedPages
            ? CalculatePooled(usages)
            : CalculatePerAsset(usages);
    }

    private static RentalMeterBillingResult CalculatePerAsset(IReadOnlyList<AssetUsage> usages)
    {
        var lines = new List<RentalMeterChargeLine>();
        foreach (var usage in usages)
        {
            var black = CalculateColor(
                usage,
                "흑백",
                usage.BlackUsage,
                usage.Asset.BlackIncludedMode,
                usage.Asset.BlackIncludedPages,
                usage.Asset.BlackOverageUnitPrice);
            if (!black.Success)
                return RentalMeterBillingResult.Review(black.Message);
            if (black.Line is not null)
                lines.Add(black.Line);

            var color = CalculateColor(
                usage,
                "컬러",
                usage.ColorUsage,
                usage.Asset.ColorIncludedMode,
                usage.Asset.ColorIncludedPages,
                usage.Asset.ColorOverageUnitPrice);
            if (!color.Success)
                return RentalMeterBillingResult.Review(color.Message);
            if (color.Line is not null)
                lines.Add(color.Line);
        }

        return RentalMeterBillingResult.Ok(lines);
    }

    private static RentalMeterBillingResult CalculatePooled(IReadOnlyList<AssetUsage> usages)
    {
        var lines = new List<RentalMeterChargeLine>();
        foreach (var colorMode in new[] { "흑백", "컬러" })
        {
            var numericUsages = new List<(AssetUsage Usage, long Pages, int Included, decimal Rate)>();
            foreach (var usage in usages)
            {
                var pages = colorMode == "흑백" ? usage.BlackUsage : usage.ColorUsage;
                var mode = RentalMeterPolicyModes.Normalize(
                    colorMode == "흑백" ? usage.Asset.BlackIncludedMode : usage.Asset.ColorIncludedMode,
                    colorMode == "흑백" ? usage.Asset.BlackIncludedPages : usage.Asset.ColorIncludedPages);
                if (string.Equals(mode, RentalMeterPolicyModes.Unlimited, StringComparison.Ordinal))
                    continue;
                if (!string.Equals(mode, RentalMeterPolicyModes.Numeric, StringComparison.Ordinal))
                    return RentalMeterBillingResult.Review($"{BuildAssetLabel(usage.Asset)}의 {colorMode} 기본 출력량이 미설정입니다.");

                var included = colorMode == "흑백" ? usage.Asset.BlackIncludedPages : usage.Asset.ColorIncludedPages;
                var rate = colorMode == "흑백" ? usage.Asset.BlackOverageUnitPrice : usage.Asset.ColorOverageUnitPrice;
                if (!included.HasValue || included.Value < 0)
                    return RentalMeterBillingResult.Review($"{BuildAssetLabel(usage.Asset)}의 {colorMode} 기본 출력량이 올바르지 않습니다.");
                if (!rate.HasValue || rate.Value < 0m)
                    return RentalMeterBillingResult.Review($"{BuildAssetLabel(usage.Asset)}의 {colorMode} 초과 장당요금이 미설정입니다.");

                numericUsages.Add((usage, pages, included.Value, rate.Value));
            }

            if (numericUsages.Count == 0)
                continue;

            var rates = numericUsages.Select(value => value.Rate).Distinct().ToList();
            if (rates.Count != 1)
                return RentalMeterBillingResult.Review($"통합계약의 {colorMode} 초과 장당요금이 장비마다 다릅니다. 동일 요금으로 맞추거나 통합계약 옵션을 해제하세요.");

            var totalUsage = numericUsages.Sum(value => value.Pages);
            var totalIncluded = numericUsages.Sum(value => (long)value.Included);
            var overage = Math.Max(0L, totalUsage - totalIncluded);
            if (overage == 0)
                continue;

            var unitPrice = rates[0];
            lines.Add(new RentalMeterChargeLine(
                colorMode,
                totalUsage,
                totalIncluded,
                overage,
                unitPrice,
                overage * unitPrice,
                numericUsages.Select(value => value.Usage.Asset.AssetId).Distinct().ToList(),
                string.Join(" / ", numericUsages.Select(value => $"{BuildAssetLabel(value.Usage.Asset)} {value.Usage.Evidence}"))));
        }

        return RentalMeterBillingResult.Ok(lines);
    }

    private static (bool Success, string Message, RentalMeterChargeLine? Line) CalculateColor(
        AssetUsage usage,
        string colorMode,
        long usagePages,
        string includedMode,
        int? includedPages,
        decimal? unitPrice)
    {
        var mode = RentalMeterPolicyModes.Normalize(includedMode, includedPages);
        if (string.Equals(mode, RentalMeterPolicyModes.Unlimited, StringComparison.Ordinal))
            return (true, string.Empty, null);
        if (!string.Equals(mode, RentalMeterPolicyModes.Numeric, StringComparison.Ordinal))
            return (false, $"{BuildAssetLabel(usage.Asset)}의 {colorMode} 기본 출력량이 미설정입니다.", null);
        if (!includedPages.HasValue || includedPages.Value < 0)
            return (false, $"{BuildAssetLabel(usage.Asset)}의 {colorMode} 기본 출력량이 올바르지 않습니다.", null);
        if (!unitPrice.HasValue || unitPrice.Value < 0m)
            return (false, $"{BuildAssetLabel(usage.Asset)}의 {colorMode} 초과 장당요금이 미설정입니다.", null);

        var overagePages = Math.Max(0L, usagePages - includedPages.Value);
        return overagePages == 0
            ? (true, string.Empty, null)
            : (true, string.Empty, new RentalMeterChargeLine(
                colorMode,
                usagePages,
                includedPages.Value,
                overagePages,
                unitPrice.Value,
                overagePages * unitPrice.Value,
                [usage.Asset.AssetId],
                usage.Evidence));
    }

    private static string BuildAssetLabel(RentalMeterAssetBillingInput asset)
    {
        var managementNumber = (asset.ManagementNumber ?? string.Empty).Trim();
        var itemName = (asset.ItemName ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(managementNumber) && !string.IsNullOrWhiteSpace(itemName))
            return $"장비 {managementNumber}({itemName})";
        if (!string.IsNullOrWhiteSpace(managementNumber))
            return $"장비 {managementNumber}";
        if (!string.IsNullOrWhiteSpace(itemName))
            return $"장비 {itemName}";
        return $"장비 {asset.AssetId:D}";
    }

    private static bool TryNormalizeBillingYearMonth(string? value, out string normalized)
    {
        var trimmed = (value ?? string.Empty).Trim();
        foreach (var format in new[] { "yyyy-MM", "yyyyMM", "yyyy-M" })
        {
            if (!DateTime.TryParseExact(
                    trimmed,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsed))
            {
                continue;
            }

            normalized = parsed.ToString("yyyy-MM", CultureInfo.InvariantCulture);
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    private static string NormalizeBillingYearMonth(string? value)
        => TryNormalizeBillingYearMonth(value, out var normalized)
            ? normalized
            : string.Empty;

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    private sealed record AssetUsage(
        RentalMeterAssetBillingInput Asset,
        long BlackUsage,
        long ColorUsage,
        string Evidence);
}
