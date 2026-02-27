using Microsoft.EntityFrameworkCore;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.Services;

/// <summary>
/// Background service that syncs local SQLite with the server every RetryMinutes.
/// Push dirty → Pull new → Mark clean.
/// Conflict resolution: server wins (server data overwrites local).
/// </summary>
public sealed class SyncService : IDisposable
{
    private readonly LocalDbContext _db;
    private readonly LocalStateService _local;
    private readonly ErpApiClient _api;
    private readonly SessionState _session;
    private Timer? _timer;
    private bool _syncInProgress;

    public event Action<string>? SyncStatusChanged;

    public SyncService(LocalDbContext db, LocalStateService local, ErpApiClient api, SessionState session)
    {
        _db = db;
        _local = local;
        _api = api;
        _session = session;
    }

    public void Start(int intervalMinutes = 3)
    {
        _timer = new Timer(async _ => await TrySyncAsync(), null,
            TimeSpan.Zero, TimeSpan.FromMinutes(intervalMinutes));
    }

    public async Task TrySyncAsync(CancellationToken ct = default)
    {
        if (!_session.IsLoggedIn) return;
        if (_syncInProgress) return;
        _syncInProgress = true;
        try
        {
            SyncStatusChanged?.Invoke("동기화 중...");
            await PushDirtyAsync(ct);
            await PullNewAsync(ct);
            SyncStatusChanged?.Invoke($"동기화 완료 {DateTime.Now:HH:mm:ss}");
        }
        catch (Exception ex)
        {
            SyncStatusChanged?.Invoke($"동기화 오류: {ex.Message}");
        }
        finally
        {
            _syncInProgress = false;
        }
    }

    // ── Push ──────────────────────────────────────────────────────────────────
    private async Task PushDirtyAsync(CancellationToken ct)
    {
        var req = new SyncPushRequest
        {
            CompanyProfiles = await _db.CompanyProfiles.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            Customers = await _db.Customers.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            Items = await _db.Items.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            Invoices = await _db.Invoices.IgnoreQueryFilters()
                .Include(i => i.Lines)
                .Include(i => i.Payments)
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct),

            Payments = await _db.Payments.IgnoreQueryFilters()
                .Where(e => e.IsDirty).AsNoTracking().ToListAsync(ct)
                .ContinueWith(t => t.Result.Select(LocalMappings.ToDto).ToList(), ct)
        };

        var hasDirty = req.CompanyProfiles.Count + req.Customers.Count +
                       req.Items.Count + req.Invoices.Count + req.Payments.Count > 0;
        if (!hasDirty) return;

        var result = await _api.PushAsync(req, ct);
        if (result is null) return;

        // Apply server-assigned invoice numbers
        foreach (var assigned in result.AssignedInvoiceNumbers)
        {
            var inv = await _db.Invoices.IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == assigned.Key, ct);
            if (inv is not null)
            {
                inv.InvoiceNumber = assigned.Value;
                inv.IsDirty = false;
            }
        }

        // Mark all pushed entities as clean
        await MarkCleanAsync<LocalCompanyProfile>(ct);
        await MarkCleanAsync<LocalCustomer>(ct);
        await MarkCleanAsync<LocalItem>(ct);
        await MarkCleanInvoicesAsync(ct);
        await MarkCleanAsync<LocalPayment>(ct);

        await _db.SaveChangesAsync(ct);
    }

    private async Task MarkCleanAsync<T>(CancellationToken ct) where T : class, ILocalSyncEntity
    {
        await _db.Set<T>().IgnoreQueryFilters()
            .Where(e => e.IsDirty)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDirty, false), ct);
    }

    private async Task MarkCleanInvoicesAsync(CancellationToken ct)
    {
        await _db.Invoices.IgnoreQueryFilters()
            .Where(e => e.IsDirty)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsDirty, false), ct);
    }

    // ── Pull ──────────────────────────────────────────────────────────────────
    private async Task PullNewAsync(CancellationToken ct)
    {
        var revStr = await _local.GetSettingAsync("LastSyncRevision", ct) ?? "0";
        var sinceRev = long.TryParse(revStr, out var r) ? r : 0L;

        var pull = await _api.PullAsync(sinceRev, ct);
        if (pull is null) return;

        await UpsertPulledAsync(pull.CompanyProfiles, _db.CompanyProfiles, LocalMappings.ToLocal, ct);
        await UpsertPulledAsync(pull.Units, _db.Units, LocalMappings.ToLocal, ct);
        await UpsertPulledAsync(pull.CustomerCategories, _db.CustomerCategories, LocalMappings.ToLocal, ct);
        await UpsertPulledAsync(pull.Customers, _db.Customers, LocalMappings.ToLocal, ct);
        await UpsertPulledAsync(pull.Items, _db.Items, LocalMappings.ToLocal, ct);
        await UpsertPulledInvoicesAsync(pull.Invoices, ct);
        await UpsertPulledAsync(pull.Payments, _db.Payments, LocalMappings.ToLocal, ct);

        if (pull.LatestRevision > sinceRev)
            await _local.SetSettingAsync("LastSyncRevision", pull.LatestRevision.ToString(), ct);
    }

    private async Task UpsertPulledAsync<TLocal, TDto>(
        IReadOnlyList<TDto> dtos,
        DbSet<TLocal> set,
        Func<TDto, TLocal> toLocal,
        CancellationToken ct)
        where TLocal : class, ILocalSyncEntity
        where TDto : class
    {
        foreach (var dto in dtos)
        {
            var local = toLocal(dto);
            local.IsDirty = false;
            var existing = await set.IgnoreQueryFilters().FirstOrDefaultAsync(e => e.Id == local.Id, ct);
            if (existing is null)
                set.Add(local);
            else
            {
                // Server wins: only overwrite if not locally dirty
                if (!existing.IsDirty)
                    _db.Entry(existing).CurrentValues.SetValues(local);
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    private async Task UpsertPulledInvoicesAsync(IReadOnlyList<InvoiceDto> dtos, CancellationToken ct)
    {
        foreach (var dto in dtos)
        {
            var local = LocalMappings.ToLocal(dto);
            local.IsDirty = false;

            var existing = await _db.Invoices.IgnoreQueryFilters()
                .Include(i => i.Lines)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == local.Id, ct);

            if (existing is null)
            {
                _db.Invoices.Add(local);
            }
            else if (!existing.IsDirty)
            {
                _db.Entry(existing).CurrentValues.SetValues(local);

                // Sync lines
                foreach (var line in local.Lines)
                {
                    var exLine = existing.Lines.FirstOrDefault(l => l.Id == line.Id);
                    if (exLine is null) existing.Lines.Add(line);
                    else _db.Entry(exLine).CurrentValues.SetValues(line);
                }
                foreach (var exLine in existing.Lines
                    .Where(l => !local.Lines.Any(ll => ll.Id == l.Id)))
                    exLine.IsDeleted = true;

                // Sync payments
                foreach (var pay in local.Payments)
                {
                    var exPay = existing.Payments.FirstOrDefault(p => p.Id == pay.Id);
                    if (exPay is null) existing.Payments.Add(pay);
                    else _db.Entry(exPay).CurrentValues.SetValues(pay);
                }
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    public void Dispose() => _timer?.Dispose();
}
