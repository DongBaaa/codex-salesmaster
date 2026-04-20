using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using Microsoft.EntityFrameworkCore;

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

        var currentRows = await _dbContext.RentalAssetAssignmentHistories
            .Where(history => history.IsCurrent)
            .ToListAsync(cancellationToken);
        var currentByAssetId = currentRows
            .GroupBy(history => history.AssetId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.LinkedAtUtc).ToList());

        foreach (var asset in assets)
        {
            currentByAssetId.TryGetValue(asset.Id, out var currentRowsForAsset);
            currentRowsForAsset ??= [];
            var desiredCustomerName = NormalizeText(string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ? asset.CustomerName : asset.CurrentCustomerName);
            var desiredInstallLocation = NormalizeText(string.IsNullOrWhiteSpace(asset.InstallLocation) ? asset.InstallSiteName : asset.InstallLocation);
            var hasDesiredAssignment = asset.BillingProfileId.HasValue
                                       || asset.CustomerId.HasValue
                                       || !string.IsNullOrWhiteSpace(desiredCustomerName)
                                       || !string.IsNullOrWhiteSpace(desiredInstallLocation);

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
                    stale.UnlinkedAtUtc ??= now;
                }

                continue;
            }

            foreach (var stale in currentRowsForAsset.Where(row => matchingCurrent is null || row.Id != matchingCurrent.Id))
            {
                stale.IsCurrent = false;
                stale.UnlinkedAtUtc ??= now;
            }

            if (matchingCurrent is not null)
                continue;

            _dbContext.RentalAssetAssignmentHistories.Add(new RentalAssetAssignmentHistory
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                BillingProfileId = asset.BillingProfileId,
                CustomerId = asset.CustomerId,
                TenantCode = asset.TenantCode,
                ResponsibleOfficeCode = asset.ResponsibleOfficeCode,
                CustomerName = desiredCustomerName,
                InstallLocation = desiredInstallLocation,
                IsCurrent = true,
                LinkedAtUtc = asset.UpdatedAtUtc == default ? now : asset.UpdatedAtUtc
            });
        }

        var activeAssetIds = assets.Select(asset => asset.Id).ToHashSet();
        foreach (var stale in currentRows.Where(row => !activeAssetIds.Contains(row.AssetId)))
        {
            stale.IsCurrent = false;
            stale.UnlinkedAtUtc ??= now;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
}
