using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

public sealed class RentalAssignmentHistoryService
{
    private readonly AppDbContext _dbContext;

    public RentalAssignmentHistoryService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var assets = await _dbContext.RentalAssets
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(asset => !asset.IsDeleted)
            .OrderBy(asset => asset.Id)
            .ToListAsync(cancellationToken);

        var allHistoryRows = await _dbContext.RentalAssetAssignmentHistories
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);
        var currentRows = allHistoryRows
            .Where(history => history.IsCurrent)
            .ToList();
        var historyById = allHistoryRows
            .GroupBy(history => history.Id)
            .ToDictionary(group => group.Key, group => group.First());
        var currentByAssetId = currentRows
            .GroupBy(history => history.AssetId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.LinkedAtUtc).ToList());

        foreach (var asset in assets)
        {
            currentByAssetId.TryGetValue(asset.Id, out var currentRowsForAsset);
            currentRowsForAsset ??= [];
            var desiredCustomerName = NormalizeText(string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ? asset.CustomerName : asset.CurrentCustomerName);
            var desiredInstallLocation = NormalizeText(string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation);
            var hasDesiredAssignment = HasCurrentAssignment(asset, desiredCustomerName, desiredInstallLocation);

            var matchingCurrent = currentRowsForAsset.FirstOrDefault(row =>
                row.BillingProfileId == asset.BillingProfileId &&
                row.CustomerId == asset.CustomerId &&
                string.Equals(row.CustomerName, desiredCustomerName, StringComparison.Ordinal) &&
                string.Equals(row.InstallLocation, desiredInstallLocation, StringComparison.Ordinal) &&
                string.Equals(row.TenantCode, asset.TenantCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(row.ResponsibleOfficeCode, asset.ResponsibleOfficeCode, StringComparison.OrdinalIgnoreCase));

            if (!hasDesiredAssignment)
            {
                foreach (var stale in currentRowsForAsset)
                {
                    stale.IsCurrent = false;
                    stale.UnlinkedAtUtc ??= NormalizeUtc(asset.LastAssignmentClearedAtUtc ?? now);
                    stale.ChangeReason = "자산 연결 해제";
                    ApplySnapshot(stale, asset);
                }

                continue;
            }

            foreach (var stale in currentRowsForAsset.Where(row => matchingCurrent is null || row.Id != matchingCurrent.Id))
            {
                stale.IsCurrent = false;
                stale.UnlinkedAtUtc ??= now;
                stale.ChangeReason = "재임대/연결 변경";
                ApplySnapshot(stale, asset);
            }

            if (matchingCurrent is not null)
            {
                ApplySnapshot(matchingCurrent, asset);
                continue;
            }

            var linkedAtUtc = ResolveAssignmentLinkedAtUtc(asset, now);
            var deterministicHistoryId = SyncIdentityGenerator.CreateRentalAssetAssignmentHistoryId(
                asset.Id,
                linkedAtUtc,
                asset.BillingProfileId,
                asset.CustomerId,
                desiredCustomerName,
                desiredInstallLocation);

            var newHistoryId = deterministicHistoryId == Guid.Empty ? Guid.NewGuid() : deterministicHistoryId;
            if (!historyById.TryGetValue(newHistoryId, out var newHistory))
            {
                newHistory = new RentalAssetAssignmentHistory { Id = newHistoryId, CreatedAtUtc = now };
                historyById[newHistoryId] = newHistory;
                _dbContext.RentalAssetAssignmentHistories.Add(newHistory);
            }

            newHistory.AssetId = asset.Id;
            newHistory.BillingProfileId = asset.BillingProfileId;
            newHistory.CustomerId = asset.CustomerId;
            newHistory.TenantCode = asset.TenantCode;
            newHistory.OfficeCode = asset.OfficeCode;
            newHistory.ResponsibleOfficeCode = asset.ResponsibleOfficeCode;
            newHistory.CustomerName = desiredCustomerName;
            newHistory.InstallLocation = desiredInstallLocation;
            newHistory.BillingProfileDisplay = BuildBillingProfileDisplay(asset);
            newHistory.ItemName = asset.ItemName;
            newHistory.MachineNumber = asset.MachineNumber;
            newHistory.ManagementNumber = asset.ManagementNumber;
            newHistory.MonthlyFee = Math.Max(0m, asset.MonthlyFee);
            newHistory.ContractStartDate = asset.ContractStartDate ?? asset.InstallDate;
            newHistory.ContractEndDate = asset.RentalEndDate;
            newHistory.ChangeReason = "서버 자산 상태 반영";
            newHistory.IsCurrent = true;
            newHistory.IsDeleted = false;
            newHistory.LinkedAtUtc = linkedAtUtc;
            newHistory.UnlinkedAtUtc = null;
            if (newHistory.CreatedAtUtc == default)
                newHistory.CreatedAtUtc = now;
            newHistory.UpdatedAtUtc = now;
        }

        var activeAssetIds = assets.Select(asset => asset.Id).ToHashSet();
        foreach (var stale in currentRows.Where(row => !activeAssetIds.Contains(row.AssetId)))
        {
            stale.IsCurrent = false;
            stale.UnlinkedAtUtc ??= now;
            stale.ChangeReason = "자산 삭제/비활성";
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static bool HasCurrentAssignment(RentalAsset asset, string desiredCustomerName, string desiredInstallLocation)
    {
        if (asset.BillingProfileId.HasValue || asset.CustomerId.HasValue)
            return true;

        if (asset.LastAssignmentClearedAtUtc.HasValue)
            return false;

        return !string.IsNullOrWhiteSpace(desiredCustomerName)
               || !string.IsNullOrWhiteSpace(desiredInstallLocation);
    }

    private static void ApplySnapshot(RentalAssetAssignmentHistory history, RentalAsset asset)
    {
        history.TenantCode = asset.TenantCode;
        history.OfficeCode = asset.OfficeCode;
        history.ResponsibleOfficeCode = asset.ResponsibleOfficeCode;
        history.BillingProfileDisplay = string.IsNullOrWhiteSpace(history.BillingProfileDisplay)
            ? BuildBillingProfileDisplay(asset)
            : history.BillingProfileDisplay;
        history.ItemName = string.IsNullOrWhiteSpace(history.ItemName) ? asset.ItemName : history.ItemName;
        history.MachineNumber = string.IsNullOrWhiteSpace(history.MachineNumber) ? asset.MachineNumber : history.MachineNumber;
        history.ManagementNumber = string.IsNullOrWhiteSpace(history.ManagementNumber) ? asset.ManagementNumber : history.ManagementNumber;
        if (history.MonthlyFee <= 0m)
            history.MonthlyFee = Math.Max(0m, asset.MonthlyFee);
        history.ContractStartDate ??= asset.ContractStartDate ?? asset.InstallDate;
        history.ContractEndDate ??= asset.RentalEndDate;
    }

    private static DateTime ResolveAssignmentLinkedAtUtc(RentalAsset asset, DateTime fallbackUtc)
    {
        if (asset.InstallDate.HasValue)
            return CreateUtcDate(asset.InstallDate.Value);
        if (asset.ContractStartDate.HasValue)
            return CreateUtcDate(asset.ContractStartDate.Value);
        if (asset.UpdatedAtUtc != default)
            return NormalizeUtc(asset.UpdatedAtUtc);
        if (asset.CreatedAtUtc != default)
            return NormalizeUtc(asset.CreatedAtUtc);
        return NormalizeUtc(fallbackUtc);
    }

    private static DateTime CreateUtcDate(DateOnly date)
        => new(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Utc);

    private static string BuildBillingProfileDisplay(RentalAsset asset)
    {
        var customerName = NormalizeText(string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ? asset.CustomerName : asset.CurrentCustomerName);
        var itemName = NormalizeText(asset.ItemName);
        if (!string.IsNullOrWhiteSpace(customerName) && !string.IsNullOrWhiteSpace(itemName))
            return $"{customerName} · {itemName}";
        return string.IsNullOrWhiteSpace(customerName) ? itemName : customerName;
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
            return DateTime.UtcNow;
        if (value.Kind == DateTimeKind.Utc)
            return value;
        if (value.Kind == DateTimeKind.Local)
            return value.ToUniversalTime();
        return DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}
