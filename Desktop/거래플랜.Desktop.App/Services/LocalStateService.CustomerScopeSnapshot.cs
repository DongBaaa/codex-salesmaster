using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    private static string? GetCustomerScopeExclusionPrefix(SessionState? session)
    {
        if (session?.User is null || session.User.UserId == Guid.Empty)
            return null;
        var owner = string.Join("\n", session.User.UserId.ToString("N"),
            session.TenantCode, session.OfficeCode, session.ScopeType,
            session.SelectedBusinessDatabaseName).ToUpperInvariant();
        return "Sync.CustomerScopeExclusion.v1." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(owner))) + ".";
    }

    private IQueryable<LocalCustomer> ApplyServerCustomerExclusions(
        IQueryable<LocalCustomer> query, SessionState? session)
    {
        var prefix = GetCustomerScopeExclusionPrefix(session);
        if (prefix is null) return query;
        var deniedIds = _db.Settings.AsNoTracking()
            .Where(setting => setting.Key.StartsWith(prefix))
            .Select(setting => setting.Value);
        return query.Where(customer => !deniedIds.Contains(customer.Id.ToString().ToUpper()));
    }

    private bool IsServerCustomerExcluded(Guid customerId, SessionState? session)
    {
        var prefix = GetCustomerScopeExclusionPrefix(session);
        if (prefix is null) return false;
        var key = prefix + customerId.ToString("N");
        return _db.Settings.AsNoTracking().Any(setting => setting.Key == key);
    }

    internal async Task<bool> ApplyServerCustomerScopeSnapshotAsync(
        CustomerScopeSnapshotDto? snapshot, SessionState session, CancellationToken ct)
    {
        if (snapshot is null) return false; // An absent old-server field is not an empty authoritative scope.
        var prefix = GetCustomerScopeExclusionPrefix(session);
        if (prefix is null || snapshot.Version != 1 || snapshot.UserId != session.User!.UserId ||
            !string.Equals(snapshot.TenantCode, session.AuthenticatedTenantCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.OfficeCode, session.OfficeCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(snapshot.ScopeType, session.ScopeType, StringComparison.OrdinalIgnoreCase) ||
            snapshot.VisibleCustomerIds is null || snapshot.VisibleCustomerIds.Contains(Guid.Empty) ||
            snapshot.VisibleCustomerIds.Distinct().Count() != snapshot.VisibleCustomerIds.Count)
            throw new InvalidDataException("Customer visibility snapshot does not match the current authenticated scope.");
        if (_db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Customer scope reconciliation requires the pull transaction.");

        var visible = snapshot.VisibleCustomerIds.ToHashSet();
        // Revision 0 is an unuploaded local create, not a revoked server record.
        var knownIds = await _db.Customers.IgnoreQueryFilters().AsNoTracking()
            .Where(customer => customer.Revision > 0)
            .Select(customer => customer.Id).ToListAsync(ct);
        var desired = knownIds.Where(id => !visible.Contains(id))
            .ToDictionary(id => prefix + id.ToString("N"), id => id.ToString().ToUpperInvariant());
        var existing = await _db.Settings.Where(setting => setting.Key.StartsWith(prefix)).ToListAsync(ct);
        var removed = existing.Where(setting => !desired.ContainsKey(setting.Key)).ToList();
        _db.Settings.RemoveRange(removed);
        var existingKeys = existing.Select(setting => setting.Key).ToHashSet(StringComparer.Ordinal);
        var changed = removed.Count > 0;
        foreach (var pair in desired)
            if (!existingKeys.Contains(pair.Key))
            {
                _db.Settings.Add(new LocalSetting { Key = pair.Key, Value = pair.Value });
                changed = true;
            }
        if (!changed) return false;
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();
        return true;
    }
}
