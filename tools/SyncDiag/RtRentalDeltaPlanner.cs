using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Tools.SyncDiag;

internal sealed record RtRentalSourceRow(
    int SourceLineNumber,
    string Status,
    string ManagementNumber,
    string ItemCategoryName,
    string ItemName,
    string Manufacturer,
    string MachineNumber,
    string CustomerName,
    string InstallLocation,
    string ManagementCompany,
    string MonthlyFeeText,
    string ContractMonthsText,
    string ContractStartDate,
    string RentalEndDate,
    string DisposalDate);

internal sealed class RtRentalDeltaPlanAudit
{
    public int SourceRowCount { get; set; }
    public int SourceTargetCompanyRowCount { get; set; }
    public int SourceOtherCompanyRowCount { get; set; }
    public int TargetAssetCount { get; set; }
    public int TargetAssetWithoutSourceCount { get; set; }
    public int MatchedUniqueKeyCount { get; set; }
    public int PlannedChangeCount { get; set; }
    public int AlreadyEqualCount { get; set; }
    public int UnmatchedSourceCount { get; set; }
    public int DuplicateSourceKeyCount { get; set; }
    public int DuplicateTargetKeyCount { get; set; }
    public int CrossOfficeExcludedCount { get; set; }
    public int CustomerMismatchExcludedCount { get; set; }
    public int StatusMismatchExcludedCount { get; set; }
    public int UnsupportedStatusExcludedCount { get; set; }
    public int InvalidScalarExcludedCount { get; set; }
    public int BillingProfileFeePreservedCount { get; set; }
    public Dictionary<string, int> ChangedFieldCounts { get; set; } =
        new(StringComparer.Ordinal);
}

internal sealed record RtRentalDeltaPlanBuildResult(
    RtRentalDeltaPlan Plan,
    RtRentalDeltaPlanAudit Audit);

internal static class RtRentalDeltaPlanner
{
    private static readonly string[] RequiredHeaders =
    [
        "Status",
        "ManagementNumber",
        "ItemCategoryName",
        "ItemName",
        "Manufacturer",
        "MachineNumber",
        "CustomerName",
        "InstallLocation",
        "ManagementCompany",
        "MonthlyFeeText",
        "ContractMonthsText",
        "ContractStartDate",
        "RentalEndDate",
        "DisposalDate"
    ];

    internal static IReadOnlyList<RtRentalSourceRow> ReadSourceCsv(string path)
    {
        using var parser = new TextFieldParser(
            path,
            Encoding.UTF8,
            detectEncoding: true)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");

        var headers = parser.ReadFields()
                      ?? throw new InvalidDataException(
                          "The RT rental source CSV has no header row.");
        var headerMap = headers
            .Select((header, index) => new
            {
                Name = (header ?? string.Empty).Trim().TrimStart('\uFEFF'),
                Index = index
            })
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Index).ToArray(),
                StringComparer.Ordinal);
        if (headerMap.Any(pair => pair.Value.Length != 1) ||
            RequiredHeaders.Any(header => !headerMap.ContainsKey(header)))
        {
            throw new InvalidDataException(
                "The RT rental source CSV headers are missing or duplicated.");
        }

        var rows = new List<RtRentalSourceRow>();
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields()
                         ?? throw new InvalidDataException(
                             "The RT rental source CSV contains an unreadable row.");
            if (fields.Length != headers.Length)
                throw new InvalidDataException(
                    $"The RT rental source CSV row {parser.LineNumber:N0} has an unexpected column count.");

            string Get(string header) =>
                (fields[headerMap[header][0]] ?? string.Empty).Trim();

            rows.Add(new RtRentalSourceRow(
                checked((int)parser.LineNumber),
                Get("Status"),
                Get("ManagementNumber"),
                Get("ItemCategoryName"),
                Get("ItemName"),
                Get("Manufacturer"),
                Get("MachineNumber"),
                Get("CustomerName"),
                Get("InstallLocation"),
                Get("ManagementCompany"),
                Get("MonthlyFeeText"),
                Get("ContractMonthsText"),
                Get("ContractStartDate"),
                Get("RentalEndDate"),
                Get("DisposalDate")));
        }

        if (rows.Count == 0)
            throw new InvalidDataException(
                "The RT rental source CSV has no data rows.");
        return rows;
    }

    internal static RtRentalDeltaPlanBuildResult BuildPlan(
        IReadOnlyCollection<RtRentalSourceRow> sourceRows,
        IReadOnlyCollection<RentalAssetDto> currentAssets,
        string businessDatabaseName,
        string sourceSha256,
        string planId,
        DateTime generatedAtUtc)
    {
        var normalizedDatabaseName = TenantScopeCatalog.GetDatabaseName(
            businessDatabaseName);
        var targetCompanyCode = string.Equals(
            normalizedDatabaseName,
            TenantScopeCatalog.GetDatabaseName(TenantScopeCatalog.Itworld),
            StringComparison.OrdinalIgnoreCase)
            ? OfficeCodeCatalog.Itworld
            : OfficeCodeCatalog.Usenet;
        var targetSourceCompany = string.Equals(
            targetCompanyCode,
            OfficeCodeCatalog.Itworld,
            StringComparison.OrdinalIgnoreCase)
            ? "아이티월드"
            : "유즈넷";
        var audit = new RtRentalDeltaPlanAudit
        {
            SourceRowCount = sourceRows.Count,
            TargetAssetCount = currentAssets.Count(asset => !asset.IsDeleted)
        };

        var sourceTargetRows = sourceRows
            .Where(row => string.Equals(
                NormalizeKey(row.ManagementCompany),
                NormalizeKey(targetSourceCompany),
                StringComparison.Ordinal))
            .ToList();
        audit.SourceTargetCompanyRowCount = sourceTargetRows.Count;
        audit.SourceOtherCompanyRowCount = sourceRows.Count - sourceTargetRows.Count;

        var sourceGroups = sourceTargetRows
            .GroupBy(row => NormalizeKey(row.ManagementNumber), StringComparer.Ordinal)
            .ToList();
        audit.DuplicateSourceKeyCount = sourceGroups
            .Where(group => string.IsNullOrEmpty(group.Key) || group.Count() != 1)
            .Sum(group => group.Count());

        var targetRows = currentAssets
            .Where(asset =>
                !asset.IsDeleted &&
                string.Equals(
                    NormalizeKey(asset.ManagementCompanyCode),
                    NormalizeKey(targetCompanyCode),
                    StringComparison.Ordinal))
            .ToList();
        var targetGroups = targetRows
            .GroupBy(asset => NormalizeKey(asset.ManagementNumber), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        audit.DuplicateTargetKeyCount = targetGroups
            .Where(pair => string.IsNullOrEmpty(pair.Key) || pair.Value.Count != 1)
            .Sum(pair => pair.Value.Count);

        var plan = new RtRentalDeltaPlan
        {
            SchemaVersion = 1,
            PlanId = planId,
            BusinessDatabaseName = normalizedDatabaseName,
            SourceSha256 = sourceSha256,
            GeneratedAtUtc = EnsureUtc(generatedAtUtc)
        };
        var matchedTargetIds = new HashSet<Guid>();

        foreach (var sourceGroup in sourceGroups.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(sourceGroup.Key) || sourceGroup.Count() != 1)
                continue;
            var source = sourceGroup.Single();
            if (!targetGroups.TryGetValue(sourceGroup.Key, out var targetGroup))
            {
                audit.UnmatchedSourceCount++;
                continue;
            }
            if (targetGroup.Count != 1)
                continue;

            var current = targetGroup[0];
            matchedTargetIds.Add(current.Id);
            audit.MatchedUniqueKeyCount++;
            if (!string.Equals(
                    current.ResponsibleOfficeCode,
                    targetCompanyCode,
                    StringComparison.OrdinalIgnoreCase))
            {
                audit.CrossOfficeExcludedCount++;
                continue;
            }

            var sourceCustomer = NormalizeDisplayKey(source.CustomerName);
            var currentCustomer = NormalizeDisplayKey(
                FirstNonEmpty(current.CurrentCustomerName, current.CustomerName));
            if (!string.Equals(sourceCustomer, currentCustomer, StringComparison.Ordinal))
            {
                audit.CustomerMismatchExcludedCount++;
                continue;
            }

            var expectedSourceStatus = MapSourceStatus(source.Status);
            if (expectedSourceStatus is null)
            {
                audit.UnsupportedStatusExcludedCount++;
                continue;
            }
            if (!string.Equals(
                    RentalAssetStatusNormalizer.Normalize(current.AssetStatus),
                    expectedSourceStatus,
                    StringComparison.OrdinalIgnoreCase))
            {
                audit.StatusMismatchExcludedCount++;
                continue;
            }

            if (!TryBuildValues(source, current, audit, out var values))
            {
                audit.InvalidScalarExcludedCount++;
                continue;
            }
            if (ValuesEqual(current, values))
            {
                audit.AlreadyEqualCount++;
                continue;
            }

            CountChangedFields(current, values, audit.ChangedFieldCounts);
            plan.Entries.Add(new RtRentalDeltaPlanEntry
            {
                AssetId = current.Id,
                ExpectedRevision = current.Revision,
                ExpectedUpdatedAtUtc = current.UpdatedAtUtc,
                ExpectedTenantCode = current.TenantCode,
                ExpectedOfficeCode = current.OfficeCode,
                ExpectedManagementCompanyCode = current.ManagementCompanyCode,
                ExpectedResponsibleOfficeCode = current.ResponsibleOfficeCode,
                ExpectedManagementNumber = current.ManagementNumber,
                ExpectedAssetStatus = current.AssetStatus,
                Values = values
            });
        }

        audit.TargetAssetWithoutSourceCount = targetRows.Count(asset =>
            !matchedTargetIds.Contains(asset.Id));
        plan.Entries = plan.Entries
            .OrderBy(entry => entry.ExpectedManagementNumber, StringComparer.Ordinal)
            .ThenBy(entry => entry.AssetId)
            .ToList();
        audit.PlannedChangeCount = plan.Entries.Count;
        return new RtRentalDeltaPlanBuildResult(plan, audit);
    }

    private static bool TryBuildValues(
        RtRentalSourceRow source,
        RentalAssetDto current,
        RtRentalDeltaPlanAudit audit,
        out RtRentalScalarValues values)
    {
        values = new RtRentalScalarValues();
        if (!TryParseDate(source.ContractStartDate, current.ContractStartDate, out var contractStartDate) ||
            !TryParseDate(source.RentalEndDate, current.RentalEndDate, out var rentalEndDate) ||
            !TryParseDate(source.DisposalDate, current.DisposalDate, out var disposalDate) ||
            !TryParseContractMonths(source.ContractMonthsText, current.ContractMonths, out var contractMonths) ||
            !TryParseMonthlyFee(source.MonthlyFeeText, current.MonthlyFee, out var monthlyFee))
        {
            return false;
        }

        if (monthlyFee != current.MonthlyFee &&
            current.BillingProfileId is Guid billingProfileId &&
            billingProfileId != Guid.Empty)
        {
            monthlyFee = current.MonthlyFee;
            audit.BillingProfileFeePreservedCount++;
        }

        values = new RtRentalScalarValues
        {
            CurrentLocation = current.CurrentLocation,
            ItemCategoryName = PreferSource(source.ItemCategoryName, current.ItemCategoryName),
            Manufacturer = PreferSource(source.Manufacturer, current.Manufacturer),
            ItemName = PreferSource(source.ItemName, current.ItemName),
            MachineNumber = PreferSource(source.MachineNumber, current.MachineNumber),
            PurchaseVendor = current.PurchaseVendor,
            PurchaseDate = current.PurchaseDate,
            DisposalDate = disposalDate,
            PurchasePrice = current.PurchasePrice,
            SalePrice = current.SalePrice,
            InstallLocation = PreferSource(source.InstallLocation, current.InstallLocation),
            DepositText = current.DepositText,
            MonthlyFee = monthlyFee,
            ContractMonths = contractMonths,
            ContractDate = current.ContractDate,
            InstallDate = current.InstallDate,
            ContractStartDate = contractStartDate,
            RentalEndDate = rentalEndDate,
            FreeSupplyItems = current.FreeSupplyItems,
            PaidSupplyItems = current.PaidSupplyItems
        };
        return true;
    }

    private static bool TryParseDate(
        string raw,
        DateOnly? fallback,
        out DateOnly? value)
    {
        var normalized = NormalizeText(raw);
        if (string.IsNullOrEmpty(normalized) || normalized == "-")
        {
            value = fallback;
            return true;
        }

        if (DateOnly.TryParseExact(
                normalized,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            value = parsed;
            return true;
        }

        value = fallback;
        return false;
    }

    private static bool TryParseContractMonths(
        string raw,
        int fallback,
        out int value)
    {
        var normalized = NormalizeText(raw);
        if (string.IsNullOrEmpty(normalized) || normalized == "-")
        {
            value = fallback;
            return true;
        }

        normalized = normalized.Replace("개월", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Trim();
        return int.TryParse(
                   normalized,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out value) &&
               value >= 0;
    }

    private static bool TryParseMonthlyFee(
        string raw,
        decimal fallback,
        out decimal value)
    {
        var normalized = NormalizeText(raw);
        if (string.IsNullOrEmpty(normalized) || normalized == "-")
        {
            value = fallback;
            return true;
        }
        if (string.Equals(normalized, "무료", StringComparison.Ordinal))
        {
            value = 0;
            return true;
        }

        normalized = normalized
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace("원", string.Empty, StringComparison.Ordinal)
            .Replace("₩", string.Empty, StringComparison.Ordinal)
            .Trim();
        return decimal.TryParse(
                   normalized,
                   NumberStyles.Number,
                   CultureInfo.InvariantCulture,
                   out value) &&
               value >= 0;
    }

    private static string? MapSourceStatus(string raw)
        => NormalizeText(raw) switch
        {
            "렌탈" => RentalAssetStatusNormalizer.Active,
            "창고" => RentalAssetStatusNormalizer.Warehouse,
            "판매" => RentalAssetStatusNormalizer.Sold,
            "폐기" => RentalAssetStatusNormalizer.Disposed,
            _ => null
        };

    private static void CountChangedFields(
        RentalAssetDto current,
        RtRentalScalarValues values,
        IDictionary<string, int> counts)
    {
        Count(nameof(values.ItemCategoryName), current.ItemCategoryName, values.ItemCategoryName);
        Count(nameof(values.Manufacturer), current.Manufacturer, values.Manufacturer);
        Count(nameof(values.ItemName), current.ItemName, values.ItemName);
        Count(nameof(values.MachineNumber), current.MachineNumber, values.MachineNumber);
        Count(nameof(values.DisposalDate), current.DisposalDate, values.DisposalDate);
        Count(nameof(values.InstallLocation), current.InstallLocation, values.InstallLocation);
        Count(nameof(values.MonthlyFee), current.MonthlyFee, values.MonthlyFee);
        Count(nameof(values.ContractMonths), current.ContractMonths, values.ContractMonths);
        Count(nameof(values.ContractStartDate), current.ContractStartDate, values.ContractStartDate);
        Count(nameof(values.RentalEndDate), current.RentalEndDate, values.RentalEndDate);
        return;

        void Count<T>(string name, T before, T after)
        {
            if (EqualityComparer<T>.Default.Equals(before, after))
                return;
            counts[name] = counts.TryGetValue(name, out var currentCount)
                ? currentCount + 1
                : 1;
        }
    }

    private static bool ValuesEqual(
        RentalAssetDto current,
        RtRentalScalarValues values)
        => string.Equals(NormalizeText(current.CurrentLocation), NormalizeText(values.CurrentLocation), StringComparison.Ordinal) &&
           string.Equals(NormalizeText(current.ItemCategoryName), NormalizeText(values.ItemCategoryName), StringComparison.Ordinal) &&
           string.Equals(NormalizeText(current.Manufacturer), NormalizeText(values.Manufacturer), StringComparison.Ordinal) &&
           string.Equals(NormalizeText(current.ItemName), NormalizeText(values.ItemName), StringComparison.Ordinal) &&
           string.Equals(NormalizeText(current.MachineNumber), NormalizeText(values.MachineNumber), StringComparison.Ordinal) &&
           string.Equals(NormalizeText(current.PurchaseVendor), NormalizeText(values.PurchaseVendor), StringComparison.Ordinal) &&
           current.PurchaseDate == values.PurchaseDate &&
           current.DisposalDate == values.DisposalDate &&
           current.PurchasePrice == values.PurchasePrice &&
           current.SalePrice == values.SalePrice &&
           string.Equals(NormalizeText(current.InstallLocation), NormalizeText(values.InstallLocation), StringComparison.Ordinal) &&
           string.Equals(NormalizeText(current.DepositText), NormalizeText(values.DepositText), StringComparison.Ordinal) &&
           current.MonthlyFee == values.MonthlyFee &&
           current.ContractMonths == values.ContractMonths &&
           current.ContractDate == values.ContractDate &&
           current.InstallDate == values.InstallDate &&
           current.ContractStartDate == values.ContractStartDate &&
           current.RentalEndDate == values.RentalEndDate &&
           string.Equals(NormalizeText(current.FreeSupplyItems), NormalizeText(values.FreeSupplyItems), StringComparison.Ordinal) &&
           string.Equals(NormalizeText(current.PaidSupplyItems), NormalizeText(values.PaidSupplyItems), StringComparison.Ordinal);

    private static string PreferSource(string source, string fallback)
        => string.IsNullOrWhiteSpace(source) ? NormalizeText(fallback) : NormalizeText(source);

    private static string FirstNonEmpty(params string?[] values)
        => values.Select(NormalizeText).FirstOrDefault(value => !string.IsNullOrEmpty(value))
           ?? string.Empty;

    private static string NormalizeKey(string? value)
        => string.Concat(
            NormalizeText(value)
                .Normalize(NormalizationForm.FormKC)
                .Where(character => !char.IsWhiteSpace(character)))
            .ToUpperInvariant();

    private static string NormalizeDisplayKey(string? value)
        => NormalizeKey(value);

    private static string NormalizeText(string? value)
        => (value ?? string.Empty).Trim();

    private static DateTime EnsureUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
}
