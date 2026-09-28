using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

// Local business rows currently have a single Guid primary key. A different
// database may legitimately use that Guid too; it must not replace this row.
internal static class SyncPullBusinessIdentityGuard
{
    private sealed record Identity(Guid Id, string Tenant, string Office, string ResponsibleOffice);

    public static async Task<string?> FindCollisionAsync(
        LocalDbContext db, SyncPullResponse pull, CancellationToken ct)
    {
        var customers = pull.Customers.Select(row => new Identity(
            row.Id, row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode)).ToList();
        if (customers.Count > 0)
        {
            var ids = customers.Select(row => row.Id).Distinct().ToList();
            var existing = await db.Customers.IgnoreQueryFilters().AsNoTracking()
                .Where(row => ids.Contains(row.Id))
                .Select(row => new Identity(row.Id, row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode))
                .ToListAsync(ct);
            if (HasCollision(customers, existing)) return "거래처";
        }

        var assets = pull.RentalAssets.Select(row => new Identity(
            row.Id, row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode)).ToList();
        if (assets.Count > 0)
        {
            var ids = assets.Select(row => row.Id).Distinct().ToList();
            var existing = await db.RentalAssets.IgnoreQueryFilters().AsNoTracking()
                .Where(row => ids.Contains(row.Id))
                .Select(row => new Identity(row.Id, row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode))
                .ToListAsync(ct);
            if (HasCollision(assets, existing)) return "렌탈 자산";
        }

        var profiles = pull.RentalBillingProfiles.Select(row => new Identity(
            row.Id, row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode)).ToList();
        if (profiles.Count > 0)
        {
            var ids = profiles.Select(row => row.Id).Distinct().ToList();
            var existing = await db.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking()
                .Where(row => ids.Contains(row.Id))
                .Select(row => new Identity(row.Id, row.TenantCode, row.OfficeCode, row.ResponsibleOfficeCode))
                .ToListAsync(ct);
            if (HasCollision(profiles, existing)) return "렌탈 청구 프로필";
        }

        return null;
    }

    private static bool HasCollision(IEnumerable<Identity> incoming, IEnumerable<Identity> existing)
    {
        var databaseById = existing.ToDictionary(row => row.Id, BusinessDatabase);
        foreach (var row in incoming)
        {
            var database = BusinessDatabase(row);
            if (databaseById.TryGetValue(row.Id, out var previous) &&
                !string.Equals(previous, database, StringComparison.OrdinalIgnoreCase))
                return true;
            // Also reject two different business owners of one ID in a fresh pull.
            databaseById[row.Id] = database;
        }
        return false;
    }

    private static string BusinessDatabase(Identity row)
        => TenantScopeCatalog.GetDatabaseName(
            TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
                row.Tenant, row.Office, fallbackOfficeCode: row.ResponsibleOffice));
}
