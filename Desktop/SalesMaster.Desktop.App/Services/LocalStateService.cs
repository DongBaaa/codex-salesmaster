using Microsoft.EntityFrameworkCore;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.Services;

/// <summary>
/// Facade over the local SQLite database for all CRUD operations.
/// All writes mark IsDirty = true; sync service flushes dirty records.
/// </summary>
public sealed class LocalStateService
{
    private readonly LocalDbContext _db;

    public LocalStateService(LocalDbContext db) => _db = db;

    // ── Customers ────────────────────────────────────────────────────────────
    public Task<List<LocalCustomer>> GetCustomersAsync(CancellationToken ct = default)
        => _db.Customers.AsNoTracking().OrderBy(c => c.NameOriginal).ToListAsync(ct);

    public async Task<LocalCustomer> UpsertCustomerAsync(LocalCustomer customer, CancellationToken ct = default)
    {
        customer.IsDirty = true;
        customer.UpdatedAtUtc = DateTime.UtcNow;
        var existing = await _db.Customers.FindAsync([customer.Id], ct);
        if (existing is null)
            _db.Customers.Add(customer);
        else
            _db.Entry(existing).CurrentValues.SetValues(customer);
        await _db.SaveChangesAsync(ct);
        return customer;
    }

    public async Task DeleteCustomerAsync(Guid id, CancellationToken ct = default)
    {
        var e = await _db.Customers.FindAsync([id], ct);
        if (e is null) return;
        e.IsDeleted = true;
        e.IsDirty = true;
        e.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ── Items ────────────────────────────────────────────────────────────────
    public Task<List<LocalItem>> GetItemsAsync(CancellationToken ct = default)
        => _db.Items.AsNoTracking().OrderBy(i => i.NameOriginal).ToListAsync(ct);

    public async Task<LocalItem> UpsertItemAsync(LocalItem item, CancellationToken ct = default)
    {
        item.IsDirty = true;
        item.UpdatedAtUtc = DateTime.UtcNow;
        var existing = await _db.Items.FindAsync([item.Id], ct);
        if (existing is null)
            _db.Items.Add(item);
        else
            _db.Entry(existing).CurrentValues.SetValues(item);
        await _db.SaveChangesAsync(ct);
        return item;
    }

    public async Task DeleteItemAsync(Guid id, CancellationToken ct = default)
    {
        var e = await _db.Items.FindAsync([id], ct);
        if (e is null) return;
        e.IsDeleted = true;
        e.IsDirty = true;
        e.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ── Invoices ─────────────────────────────────────────────────────────────
    public Task<List<LocalInvoice>> GetInvoicesAsync(
        DateOnly? from = null, DateOnly? to = null,
        Guid? customerId = null, CancellationToken ct = default)
    {
        var q = _db.Invoices
            .Include(i => i.Lines.Where(l => !l.IsDeleted))
            .Include(i => i.Payments.Where(p => !p.IsDeleted))
            .AsNoTracking();

        if (from.HasValue) q = q.Where(i => i.InvoiceDate >= from.Value);
        if (to.HasValue) q = q.Where(i => i.InvoiceDate <= to.Value);
        if (customerId.HasValue) q = q.Where(i => i.CustomerId == customerId.Value);

        return q.OrderByDescending(i => i.InvoiceDate)
                .ThenByDescending(i => i.InvoiceNumber)
                .ToListAsync(ct);
    }

    public async Task<LocalInvoice?> GetInvoiceAsync(Guid id, CancellationToken ct = default)
        => await _db.Invoices
            .Include(i => i.Lines.Where(l => !l.IsDeleted))
            .Include(i => i.Payments.Where(p => !p.IsDeleted))
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<LocalInvoice> SaveInvoiceAsync(LocalInvoice invoice, CancellationToken ct = default)
    {
        invoice.IsDirty = true;
        invoice.UpdatedAtUtc = DateTime.UtcNow;

        // Recalculate amounts
        invoice.TotalAmount = invoice.Lines.Where(l => !l.IsDeleted).Sum(l => l.LineAmount);
        invoice.SupplyAmount = Math.Round(invoice.TotalAmount / 1.1m, 0, MidpointRounding.AwayFromZero);
        invoice.VatAmount = invoice.TotalAmount - invoice.SupplyAmount;

        var existing = await _db.Invoices
            .Include(i => i.Lines)
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.Id == invoice.Id, ct);

        if (existing is null)
        {
            // Assign local temp number if no server number yet
            if (string.IsNullOrEmpty(invoice.InvoiceNumber) && string.IsNullOrEmpty(invoice.LocalTempNumber))
            {
                var ym = invoice.InvoiceDate.ToString("yyyyMM");
                var todayMax = await _db.Invoices
                    .Where(i => i.LocalTempNumber.StartsWith($"L{ym}-"))
                    .CountAsync(ct);
                invoice.LocalTempNumber = $"L{ym}-{(todayMax + 1):D4}";
            }
            _db.Invoices.Add(invoice);
        }
        else
        {
            _db.Entry(existing).CurrentValues.SetValues(invoice);

            // Sync lines
            foreach (var line in invoice.Lines)
            {
                var exLine = existing.Lines.FirstOrDefault(l => l.Id == line.Id);
                if (exLine is null)
                    existing.Lines.Add(line);
                else
                    _db.Entry(exLine).CurrentValues.SetValues(line);
            }
            // Mark removed lines as deleted
            foreach (var exLine in existing.Lines)
            {
                if (!invoice.Lines.Any(l => l.Id == exLine.Id))
                    exLine.IsDeleted = true;
            }
        }

        await _db.SaveChangesAsync(ct);
        return invoice;
    }

    public async Task DeleteInvoiceAsync(Guid id, CancellationToken ct = default)
    {
        var e = await _db.Invoices.FindAsync([id], ct);
        if (e is null) return;
        e.IsDeleted = true;
        e.IsDirty = true;
        e.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ── Payments ─────────────────────────────────────────────────────────────
    public async Task<LocalPayment> SavePaymentAsync(LocalPayment payment, CancellationToken ct = default)
    {
        payment.IsDirty = true;
        payment.UpdatedAtUtc = DateTime.UtcNow;
        var existing = await _db.Payments.FindAsync([payment.Id], ct);
        if (existing is null)
            _db.Payments.Add(payment);
        else
            _db.Entry(existing).CurrentValues.SetValues(payment);
        await _db.SaveChangesAsync(ct);
        return payment;
    }

    public async Task DeletePaymentAsync(Guid id, CancellationToken ct = default)
    {
        var e = await _db.Payments.FindAsync([id], ct);
        if (e is null) return;
        e.IsDeleted = true;
        e.IsDirty = true;
        e.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ── CompanyProfile ────────────────────────────────────────────────────────
    public Task<LocalCompanyProfile?> GetCompanyProfileAsync(CancellationToken ct = default)
        => _db.CompanyProfiles.AsNoTracking().FirstOrDefaultAsync(ct);

    public async Task SaveCompanyProfileAsync(LocalCompanyProfile profile, CancellationToken ct = default)
    {
        profile.IsDirty = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        var existing = await _db.CompanyProfiles.FindAsync([profile.Id], ct);
        if (existing is null)
            _db.CompanyProfiles.Add(profile);
        else
            _db.Entry(existing).CurrentValues.SetValues(profile);
        await _db.SaveChangesAsync(ct);
    }

    // ── Units & Categories ────────────────────────────────────────────────────
    public Task<List<LocalUnit>> GetUnitsAsync(CancellationToken ct = default)
        => _db.Units.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.Name).ToListAsync(ct);

    public Task<List<LocalCustomerCategory>> GetCategoriesAsync(CancellationToken ct = default)
        => _db.CustomerCategories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

    // ── Settings ──────────────────────────────────────────────────────────────
    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        var s = await _db.Settings.FindAsync([key], ct);
        return s?.Value;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        var s = await _db.Settings.FindAsync([key], ct);
        if (s is null)
            _db.Settings.Add(new LocalSetting { Key = key, Value = value });
        else
            s.Value = value;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<string?> GetInvoicePrintPayloadAsync(Guid invoiceId, CancellationToken ct = default)
    {
        var key = BuildInvoicePrintSettingKey(invoiceId);
        var setting = await _db.Settings.FindAsync([key], ct);
        return setting?.Value;
    }

    public async Task SaveInvoicePrintPayloadAsync(Guid invoiceId, string payloadJson, CancellationToken ct = default)
    {
        var key = BuildInvoicePrintSettingKey(invoiceId);
        var setting = await _db.Settings.FindAsync([key], ct);
        if (setting is null)
            _db.Settings.Add(new LocalSetting { Key = key, Value = payloadJson ?? string.Empty });
        else
            setting.Value = payloadJson ?? string.Empty;

        await _db.SaveChangesAsync(ct);
    }

    private static string BuildInvoicePrintSettingKey(Guid invoiceId)
        => $"InvoicePrint:{invoiceId:N}";

    // ── Session cache (offline fallback) ─────────────────────────────────────
    public async Task SaveSessionCacheAsync(string username, string role, IEnumerable<string> permissions, CancellationToken ct = default)
    {
        await SetSettingAsync($"CachedSession_Username", username, ct);
        await SetSettingAsync($"CachedSession_Role", role, ct);
        await SetSettingAsync($"CachedSession_Permissions", string.Join(",", permissions), ct);
    }

    public async Task<SalesMaster.Shared.Contracts.UserSessionDto?> GetCachedSessionAsync(string username, CancellationToken ct = default)
    {
        var cachedUsername = await GetSettingAsync("CachedSession_Username", ct);
        if (!string.Equals(cachedUsername, username, StringComparison.OrdinalIgnoreCase))
            return null;

        var role = await GetSettingAsync("CachedSession_Role", ct) ?? "User";
        var permsRaw = await GetSettingAsync("CachedSession_Permissions", ct) ?? string.Empty;
        var permissions = permsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

        return new SalesMaster.Shared.Contracts.UserSessionDto
        {
            UserId = Guid.Empty,
            Username = cachedUsername,
            Role = role,
            Permissions = permissions
        };
    }

    // ── Transactions (수금/지불) ──────────────────────────────────────────────
    public Task<List<LocalTransaction>> GetTransactionsAsync(Guid customerId, CancellationToken ct = default)
        => _db.Transactions
            .AsNoTracking()
            .Where(t => t.CustomerId == customerId)
            .OrderByDescending(t => t.TransactionDate)
            .ToListAsync(ct);

    public async Task<LocalTransaction> SaveTransactionAsync(LocalTransaction t, CancellationToken ct = default)
    {
        t.IsDirty = true;
        t.UpdatedAtUtc = DateTime.UtcNow;
        var existing = await _db.Transactions.FindAsync([t.Id], ct);
        if (existing is null)
            _db.Transactions.Add(t);
        else
            _db.Entry(existing).CurrentValues.SetValues(t);
        await _db.SaveChangesAsync(ct);
        return t;
    }

    // ── Dirty-entity counts ───────────────────────────────────────────────────
    public async Task<int> CountDirtyAsync(CancellationToken ct = default)
    {
        var count = 0;
        count += await _db.CompanyProfiles.IgnoreQueryFilters().CountAsync(e => e.IsDirty, ct);
        count += await _db.Customers.IgnoreQueryFilters().CountAsync(e => e.IsDirty, ct);
        count += await _db.Items.IgnoreQueryFilters().CountAsync(e => e.IsDirty, ct);
        count += await _db.Invoices.IgnoreQueryFilters().CountAsync(e => e.IsDirty, ct);
        count += await _db.Payments.IgnoreQueryFilters().CountAsync(e => e.IsDirty, ct);
        return count;
    }
}
