using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;

var command = args.FirstOrDefault()?.Trim().ToLowerInvariant();
if (string.IsNullOrWhiteSpace(command))
{
    Console.Error.WriteLine("usage: SyncDiag <prepare-test-seed|preseed-sync|mark-all-dirty|sync|maintenance-sync|inspect|stored-credentials>");
    return 2;
}

try
{
    await using var db = new LocalDbContext();
    await LocalDbInitializer.InitializeAsync(db);

    switch (command)
    {
        case "prepare-test-seed":
            await PrepareTestSeedAsync(db);
            return 0;
        case "preseed-sync":
            Console.WriteLine("sync_ok=True");
            return 0;
        case "mark-all-dirty":
            var markedCount = await MarkAllDirtyAsync(db);
            Console.WriteLine($"marked_dirty={markedCount}");
            return 0;
        case "sync":
            return await RunSyncAsync(db);
        case "maintenance-sync":
            return await RunSyncAsync(db);
        case "inspect":
            await PrintDirtyInspectionAsync(db);
            return 0;
        case "stored-credentials":
            await PrintStoredCredentialsAsync(db);
            return 0;
        default:
            Console.Error.WriteLine($"unknown command: {command}");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

static async Task PrintDirtyInspectionAsync(LocalDbContext db)
{
    var currentScopeDirty = await TryCountCurrentScopeDirtyAsync(db);
    if (currentScopeDirty.HasValue)
    {
        Console.WriteLine($"current_scope_dirty={currentScopeDirty.Value}");
    }

    Console.WriteLine($"customers_dirty={await CountDirtyAsync(db.Customers.IgnoreQueryFilters())}");
    Console.WriteLine($"contracts_dirty={await CountDirtyAsync(db.CustomerContracts.IgnoreQueryFilters())}");
    Console.WriteLine($"items_dirty={await CountDirtyAsync(db.Items.IgnoreQueryFilters())}");
    Console.WriteLine($"invoices_dirty={await CountDirtyAsync(db.Invoices.IgnoreQueryFilters())}");
    Console.WriteLine($"payments_dirty={await CountDirtyAsync(db.Payments.IgnoreQueryFilters())}");
    Console.WriteLine($"transactions_dirty={await CountDirtyAsync(db.Transactions.IgnoreQueryFilters())}");
    Console.WriteLine($"rental_profiles_dirty={await CountDirtyAsync(db.RentalBillingProfiles.IgnoreQueryFilters())}");
    Console.WriteLine($"rental_assets_dirty={await CountDirtyAsync(db.RentalAssets.IgnoreQueryFilters())}");
    Console.WriteLine($"rental_asset_histories_dirty={await CountDirtyAsync(db.RentalAssetAssignmentHistories.IgnoreQueryFilters())}");
    Console.WriteLine($"outbox_count={await db.SyncOutboxEntries.CountAsync()}");
}

static async Task<int?> TryCountCurrentScopeDirtyAsync(LocalDbContext db)
{
    var username = Environment.GetEnvironmentVariable("GEORAEPLAN_SYNC_USERNAME");
    var password = Environment.GetEnvironmentVariable("GEORAEPLAN_SYNC_PASSWORD");
    var baseUrl = Environment.GetEnvironmentVariable("GEORAEPLAN_SYNC_BASEURL");

    if (string.IsNullOrWhiteSpace(username) ||
        string.IsNullOrEmpty(password) ||
        string.IsNullOrWhiteSpace(baseUrl))
    {
        return null;
    }

    var session = new SessionState();
    using var http = new HttpClient
    {
        BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(120)
    };

    var api = new ErpApiClient(http, session);
    var login = await api.LoginAsync(username, password);
    if (login is null || string.IsNullOrWhiteSpace(login.Token))
    {
        throw new InvalidOperationException("inspect_login_failed=True");
    }

    session.SetSession(login.Token, login.User);
    var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
    return await local.CountDirtyAsync(session);
}

static Task<int> CountDirtyAsync<TEntity>(IQueryable<TEntity> query)
    where TEntity : class, ILocalSyncEntity
    => query.CountAsync(entity => entity.IsDirty);

static async Task PrepareTestSeedAsync(LocalDbContext db)
{
    Directory.CreateDirectory(Path.GetDirectoryName(거래플랜.Desktop.App.Infrastructure.AppPaths.LocalDbFile)!);
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);");
    Console.WriteLine("prepare_ok=True");
}

static async Task PrintStoredCredentialsAsync(LocalDbContext db)
{
    var session = new SessionState();
    var dispatcher = new SyncRequestDispatcher();
    var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
    var credentials = await local.GetStoredSyncCredentialsAsync();
    var payload = credentials.Select(credential => new
    {
        credential.OfficeCode,
        credential.TenantCode,
        credential.Username,
        credential.Password,
        SavedAtUtc = credential.SavedAtUtc.ToString("O")
    });

    Console.WriteLine(JsonSerializer.Serialize(payload));
}

static async Task<int> RunSyncAsync(LocalDbContext db)
{
    var username = Environment.GetEnvironmentVariable("GEORAEPLAN_SYNC_USERNAME");
    var password = Environment.GetEnvironmentVariable("GEORAEPLAN_SYNC_PASSWORD");
    var baseUrl = Environment.GetEnvironmentVariable("GEORAEPLAN_SYNC_BASEURL");

    if (string.IsNullOrWhiteSpace(username) ||
        string.IsNullOrEmpty(password) ||
        string.IsNullOrWhiteSpace(baseUrl))
    {
        Console.Error.WriteLine("GEORAEPLAN_SYNC_USERNAME/PASSWORD/BASEURL 환경변수가 필요합니다.");
        return 1;
    }

    var session = new SessionState();
    using var http = new HttpClient
    {
        BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
        Timeout = TimeSpan.FromSeconds(120)
    };

    var api = new ErpApiClient(http, session);
    var login = await api.LoginAsync(username, password);
    if (login is null || string.IsNullOrWhiteSpace(login.Token))
    {
        Console.Error.WriteLine("login_failed=True");
        return 1;
    }

    session.SetSession(login.Token, login.User);
    var dispatcher = new SyncRequestDispatcher();
    var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
    var rental = new RentalStateService(db, local);
    var diagnostics = new SyncDiagnosticsService(session);
    using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);

    var ok = await sync.TrySyncAsync();
    Console.WriteLine($"sync_ok={ok}");
    Console.WriteLine($"dirty_count={await local.CountDirtyAsync(session)}");
    return ok ? 0 : 1;
}

static async Task<int> MarkAllDirtyAsync(LocalDbContext db)
{
    await db.SyncOutboxEntries.ExecuteDeleteAsync();
    var count = 0;
    count += await MarkDirtyAsync(db.CustomerMasters.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.Customers.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.CustomerContracts.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.Items.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.Invoices.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.Payments.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.Transactions.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.TransactionAttachments.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.InventoryTransfers.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.RentalManagementCompanies.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.RentalBillingProfiles.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.RentalAssets.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.RentalAssetAssignmentHistories.IgnoreQueryFilters());
    count += await MarkDirtyAsync(db.RentalBillingLogs.IgnoreQueryFilters());
    return count;
}

static Task<int> MarkDirtyAsync<TEntity>(IQueryable<TEntity> query)
    where TEntity : class, ILocalSyncEntity
{
    var now = DateTime.UtcNow;
    return query.ExecuteUpdateAsync(setters => setters
        .SetProperty(entity => entity.IsDirty, true)
        .SetProperty(entity => entity.UpdatedAtUtc, now));
}
