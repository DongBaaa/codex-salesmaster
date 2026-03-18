using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// Background sync service: push local dirty rows, then pull latest rows.
/// </summary>
public sealed class SyncService : IDisposable
{
    private const int MaxRetryCount = 3;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);

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

    public void Start(int intervalMinutes = 3, bool runImmediately = false)
    {
        _timer?.Dispose();
        var due = runImmediately ? TimeSpan.Zero : TimeSpan.FromMinutes(intervalMinutes);
        _timer = new Timer(_ => _ = TrySyncAsync(), null,
            due, TimeSpan.FromMinutes(intervalMinutes));
    }

    public async Task<bool> TrySyncAsync(CancellationToken ct = default)
    {
        if (!_session.IsLoggedIn)
            return false;
        if (_syncInProgress)
            return false;

        _syncInProgress = true;
        try
        {
            SetStatus("동기화 중...");
            AppLogger.Info("SYNC", "동기화 시작");

            await ExecuteWithRetryAsync(PushDirtyAsync, "업로드", ct);
            await ExecuteWithRetryAsync(PullNewAsync, "다운로드", ct);

            await _local.SetSettingAsync("Sync.LastSuccessAt", DateTime.Now.ToString("O"), CancellationToken.None);
            SetStatus($"동기화 완료 {DateTime.Now:HH:mm:ss}");
            AppLogger.Info("SYNC", "동기화 완료");
            return true;
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            if (detail.Length > 220)
                detail = detail[..220] + "...";

            await _local.SetSettingAsync(
                "Sync.LastError",
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {detail}",
                CancellationToken.None);

            SetStatus($"동기화 오류: {detail}");
            AppLogger.Error("SYNC", "동기화 실패", ex);
            return false;
        }
        finally
        {
            _syncInProgress = false;
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ex is TaskCanceledException && !ct.IsCancellationRequested)
            return true;

        if (ex is TimeoutException)
            return true;

        if (ex is HttpRequestException httpEx)
            return httpEx.StatusCode is null;

        return false;
    }

    private async Task ExecuteWithRetryAsync(
        Func<CancellationToken, Task> operation,
        string operationName,
        CancellationToken ct)
    {
        var delay = InitialRetryDelay;

        for (var attempt = 1; attempt <= MaxRetryCount; attempt++)
        {
            try
            {
                await operation(ct);
                if (attempt > 1)
                {
                    var recoveredMessage = $"?숆린??{operationName} 蹂듦뎄??({attempt}/{MaxRetryCount})";
                    SetStatus(recoveredMessage);
                    AppLogger.Info("SYNC", recoveredMessage);
                }
                return;
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < MaxRetryCount)
            {
                var retryMessage = $"동기화 {operationName} 실패 ({attempt}/{MaxRetryCount}), {delay.TotalSeconds:0}초 후 재시도";
                SetStatus(retryMessage);
                AppLogger.Warn("SYNC", $"{retryMessage}: {ex.Message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
        }

        await operation(ct);
    }

    private void SetStatus(string message) => SyncStatusChanged?.Invoke(message);

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
        if (!hasDirty)
            return;

        var result = await _api.PushAsync(req, ct);
        if (result is null)
            return;

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

    private async Task PullNewAsync(CancellationToken ct)
    {
        var revStr = await _local.GetSettingAsync("LastSyncRevision", ct) ?? "0";
        var sinceRev = long.TryParse(revStr, out var r) ? r : 0L;

        var pull = await _api.PullAsync(sinceRev, ct);
        if (pull is null)
            return;

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
            {
                set.Add(local);
            }
            else
            {
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

                foreach (var line in local.Lines)
                {
                    var exLine = existing.Lines.FirstOrDefault(l => l.Id == line.Id);
                    if (exLine is null)
                        existing.Lines.Add(line);
                    else
                        _db.Entry(exLine).CurrentValues.SetValues(line);
                }

                foreach (var exLine in existing.Lines.Where(l => !local.Lines.Any(ll => ll.Id == l.Id)))
                    exLine.IsDeleted = true;

                foreach (var pay in local.Payments)
                {
                    var exPay = existing.Payments.FirstOrDefault(p => p.Id == pay.Id);
                    if (exPay is null)
                        existing.Payments.Add(pay);
                    else
                        _db.Entry(exPay).CurrentValues.SetValues(pay);
                }
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    public void Dispose() => _timer?.Dispose();
}

