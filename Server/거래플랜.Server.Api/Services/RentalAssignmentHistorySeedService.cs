using System.Globalization;
using System.Text.Json;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public sealed class RentalAssignmentHistorySeedService
{
    private const string SeedId = "rental-assignment-history-workbook-20260428-v2-install-date";
    private const string SeedFileName = "rental-assignment-history-workbook-seed.json";
    private static readonly TimeZoneInfo KoreaTimeZone = ResolveKoreaTimeZone();

    private readonly AppDbContext _dbContext;

    public RentalAssignmentHistorySeedService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task ApplyWorkbookSeedAsync(CancellationToken cancellationToken = default)
    {
        if (await _dbContext.ProcessedSyncMutations.AnyAsync(row => row.MutationId == SeedId, cancellationToken))
            return;

        var rows = await LoadSeedRowsAsync(cancellationToken);
        if (rows.Count == 0)
            return;

        var now = DateTime.UtcNow;
        var assets = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .Where(asset => !asset.IsDeleted)
            .ToListAsync(cancellationToken);
        if (assets.Count == 0)
            return;

        var histories = await _dbContext.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        var historiesById = histories
            .GroupBy(history => history.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var activatedSeedHistoryIds = new HashSet<Guid>();

        foreach (var history in histories)
        {
            history.IsCurrent = false;
            history.IsDeleted = true;
            history.UnlinkedAtUtc ??= now;
            history.ChangeReason = "엑셀 임대이력 재반영 전 기존 이력 정리";
            history.UpdatedAtUtc = now;
        }

        var created = new List<RentalAssetAssignmentHistory>();
        foreach (var row in rows)
        {
            var asset = ResolveAsset(row, assets);
            if (asset is null)
                continue;

            var installDate = TryParseDate(row.InstallDate, out var parsedInstallDate)
                ? parsedInstallDate
                : (DateOnly?)null;
            var contractStartDate = TryParseDate(row.ContractStartDate, out var parsedContractStartDate)
                ? parsedContractStartDate
                : (DateOnly?)null;
            var rentalEndDate = TryParseDate(row.RentalEndDate, out var parsedRentalEndDate)
                ? parsedRentalEndDate
                : (DateOnly?)null;

            var assetChanged = false;
            if (installDate.HasValue)
                assetChanged |= SetIfDifferent(value => asset.InstallDate = value, asset.InstallDate, installDate);
            if (contractStartDate.HasValue)
                assetChanged |= SetIfDifferent(value => asset.ContractStartDate = value, asset.ContractStartDate, contractStartDate);
            if (rentalEndDate.HasValue)
                assetChanged |= SetIfDifferent(value => asset.RentalEndDate = value, asset.RentalEndDate, rentalEndDate);
            if (assetChanged)
                asset.UpdatedAtUtc = now;

            foreach (var entry in row.Histories.OrderBy(history => history.Sequence))
            {
                if (!TryParseDate(entry.ReturnedDate, out var returnedDate))
                    continue;

                var unlinkedAtUtc = ConvertKoreaDateToUtc(returnedDate);
                var linkedAtUtc = unlinkedAtUtc.AddSeconds(-Math.Max(1, entry.Sequence));
                var customerName = NormalizeText(entry.CustomerName);
                var historyId = CreateUniqueWorkbookHistoryId(historiesById, activatedSeedHistoryIds);
                var isNew = !historiesById.TryGetValue(historyId, out var history);
                if (isNew)
                {
                    history = new RentalAssetAssignmentHistory { Id = historyId };
                    historiesById[historyId] = history;
                    created.Add(history);
                }

                ArgumentNullException.ThrowIfNull(history);
                history.AssetId = asset.Id;
                history.BillingProfileId = asset.BillingProfileId;
                history.CustomerId = asset.CustomerId;
                history.TenantCode = asset.TenantCode;
                history.OfficeCode = asset.OfficeCode;
                history.ResponsibleOfficeCode = asset.ResponsibleOfficeCode;
                history.CustomerName = customerName;
                history.InstallLocation = customerName;
                history.BillingProfileDisplay = BuildBillingProfileDisplay(asset, customerName);
                history.ItemName = FirstNonEmpty(row.ItemName, asset.ItemName);
                history.MachineNumber = FirstNonEmpty(row.MachineNumber, asset.MachineNumber);
                history.ManagementNumber = FirstNonEmpty(row.ManagementNumber, asset.ManagementNumber);
                history.MonthlyFee = TryParseMoney(row.MonthlyFeeText, out var monthlyFee)
                    ? monthlyFee
                    : Math.Max(0m, asset.MonthlyFee);
                history.ContractStartDate = null;
                history.ContractEndDate = rentalEndDate ?? asset.RentalEndDate;
                history.ChangeReason = $"엑셀 회수이력(회수{entry.Sequence}, 원본 {row.RowNumber}행)";
                history.IsCurrent = false;
                history.LinkedAtUtc = linkedAtUtc;
                history.UnlinkedAtUtc = unlinkedAtUtc;
                if (isNew || history.CreatedAtUtc == default)
                    history.CreatedAtUtc = now;
                history.UpdatedAtUtc = now;
                history.IsDeleted = false;
            }
        }

        if (created.Count > 0)
            _dbContext.RentalAssetAssignmentHistories.AddRange(created);

        _dbContext.ProcessedSyncMutations.Add(new ProcessedSyncMutation
        {
            MutationId = SeedId,
            DeviceId = "server-maintenance",
            EntityName = nameof(RentalAssetAssignmentHistory),
            EntityId = SeedId,
            ExpectedRevision = 0,
            ProcessedAtUtc = now
        });

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static async Task<List<WorkbookSeedRow>> LoadSeedRowsAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "Seeds", SeedFileName);
        if (!File.Exists(path))
            return [];

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<WorkbookSeedRow>>(
                   stream,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true },
                   cancellationToken)
               ?? [];
    }

    private static RentalAsset? ResolveAsset(WorkbookSeedRow row, IReadOnlyCollection<RentalAsset> assets)
    {
        var officeCode = ResolveManagementCompanyOffice(row.ManagementCompanyName);
        var candidates = assets.Where(asset =>
            string.IsNullOrWhiteSpace(officeCode) ||
            string.Equals(asset.ManagementCompanyCode, officeCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(asset.OfficeCode, officeCode, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(asset.ResponsibleOfficeCode, officeCode, StringComparison.OrdinalIgnoreCase)).ToList();

        return FindUnique(candidates, asset => asset.ManagementNumber, row.ManagementNumber)
               ?? FindUnique(candidates, asset => asset.ManagementId, row.ManagementId)
               ?? FindUnique(candidates, asset => asset.MachineNumber, row.MachineNumber)
               ?? FindUnique(assets, asset => asset.ManagementNumber, row.ManagementNumber)
               ?? FindUnique(assets, asset => asset.ManagementId, row.ManagementId)
               ?? FindUnique(assets, asset => asset.MachineNumber, row.MachineNumber);
    }

    private static RentalAsset? FindUnique(IEnumerable<RentalAsset> assets, Func<RentalAsset, string?> selector, string? expected)
    {
        var normalizedExpected = NormalizeKey(expected);
        if (string.IsNullOrWhiteSpace(normalizedExpected))
            return null;

        var matches = assets
            .Where(asset => string.Equals(NormalizeKey(selector(asset)), normalizedExpected, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string ResolveManagementCompanyOffice(string? value)
    {
        var normalized = NormalizeKey(value);
        if (normalized.Contains("아이티월드", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("ITWORLD", StringComparison.OrdinalIgnoreCase))
        {
            return OfficeCodeCatalog.Itworld;
        }

        if (normalized.Contains("유즈넷", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("USENET", StringComparison.OrdinalIgnoreCase))
        {
            return OfficeCodeCatalog.Usenet;
        }

        return string.Empty;
    }

    private static bool TryParseDate(string? value, out DateOnly date)
    {
        var text = NormalizeText(value);
        if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;
        if (DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out var dateTime))
        {
            date = DateOnly.FromDateTime(dateTime);
            return true;
        }

        return false;
    }

    private static DateTime ConvertKoreaDateToUtc(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, KoreaTimeZone);
    }

    private static bool TryParseMoney(string? value, out decimal amount)
    {
        var text = NormalizeText(value).Replace(",", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(text) || text.Contains("무료", StringComparison.OrdinalIgnoreCase))
        {
            amount = 0m;
            return true;
        }

        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount);
    }

    private static string BuildBillingProfileDisplay(RentalAsset asset, string customerName)
    {
        var itemName = NormalizeText(asset.ItemName);
        if (!string.IsNullOrWhiteSpace(customerName) && !string.IsNullOrWhiteSpace(itemName))
            return $"{customerName} · {itemName}";
        return FirstNonEmpty(customerName, itemName);
    }

    private static Guid CreateUniqueWorkbookHistoryId(
        IReadOnlyDictionary<Guid, RentalAssetAssignmentHistory> existingHistories,
        ISet<Guid> reservedIds)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var historyId = Guid.NewGuid();
            if (!existingHistories.ContainsKey(historyId) && reservedIds.Add(historyId))
                return historyId;
        }

        Guid fallbackId;
        do
        {
            fallbackId = Guid.NewGuid();
        }
        while (existingHistories.ContainsKey(fallbackId) || !reservedIds.Add(fallbackId));
        return fallbackId;
    }

    private static bool SetIfDifferent<T>(Action<T> setter, T current, T next)
    {
        if (EqualityComparer<T>.Default.Equals(current, next))
            return false;
        setter(next);
        return true;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.Select(NormalizeText).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static string NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string NormalizeKey(string? value)
        => NormalizeText(value).Replace(" ", string.Empty, StringComparison.Ordinal);

    private static TimeZoneInfo ResolveKoreaTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Seoul");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time");
        }
    }

    private sealed class WorkbookSeedRow
    {
        public int RowNumber { get; set; }
        public string ManagementId { get; set; } = string.Empty;
        public string ManagementNumber { get; set; } = string.Empty;
        public string ManagementCompanyName { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public string MachineNumber { get; set; } = string.Empty;
        public string CurrentCustomerName { get; set; } = string.Empty;
        public string CurrentInstallLocation { get; set; } = string.Empty;
        public string MonthlyFeeText { get; set; } = string.Empty;
        public string InstallDate { get; set; } = string.Empty;
        public string ContractStartDate { get; set; } = string.Empty;
        public string RentalEndDate { get; set; } = string.Empty;
        public List<WorkbookSeedHistory> Histories { get; set; } = [];
    }

    private sealed class WorkbookSeedHistory
    {
        public int Sequence { get; set; }
        public string ReturnedDate { get; set; } = string.Empty;
        public string CustomerName { get; set; } = string.Empty;
    }
}
