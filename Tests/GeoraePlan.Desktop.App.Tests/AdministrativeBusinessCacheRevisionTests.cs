using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AdministrativeBusinessCacheRevisionTests
{
    [Fact]
    public async Task SharedMirrorReset_ClearsAdministrativeBusinessCacheRevisions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();
        var session = CreateAdminSession();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        await local.SetSettingAsync("Sync.AdminBusinessCacheRevision.USENET", "100");
        await local.SetSettingAsync("Sync.AdminBusinessCacheRevision.ITWORLD", "200");

        await local.ResetSharedMirrorCacheAsync();

        Assert.Null(await local.GetSettingAsync("Sync.AdminBusinessCacheRevision.USENET"));
        Assert.Null(await local.GetSettingAsync("Sync.AdminBusinessCacheRevision.ITWORLD"));
    }

    [Fact]
    public async Task AdministrativeBusinessCache_ReusesPersistedRevisionAfterServiceRestart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();

        var session = CreateAdminSession();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rental = new RentalStateService(db);
        var diagnostics = new SyncDiagnosticsService(session);
        var handler = new AdministrativeCachePullHandler();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);

        using (var firstSync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics))
        {
            Assert.True(await firstSync.EnsureAdministrativeBusinessCachesAsync());
        }

        Assert.Equal("100", await local.GetSettingAsync("Sync.AdminBusinessCacheRevision.USENET"));
        Assert.Equal("200", await local.GetSettingAsync("Sync.AdminBusinessCacheRevision.ITWORLD"));

        handler.ClearRequests();
        using (var restartedSync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics))
        {
            Assert.True(await restartedSync.EnsureAdministrativeBusinessCachesAsync());
        }

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(handler.Requests, request => request.DatabaseName == "USENET" && request.SinceRevision == 100);
        Assert.Contains(handler.Requests, request => request.DatabaseName == "ITWORLD" && request.SinceRevision == 200);
        Assert.All(handler.Requests, request => Assert.True(request.RentalAdministrationOnly));
    }

    [Fact]
    public async Task AdministrativeBusinessCache_RentalOnlyPullPreservesCleanStocksAcrossTenants()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();

        var now = new DateTime(
            2026,
            7,
            31,
            1,
            0,
            0,
            DateTimeKind.Utc);
        var usenetItemId =
            Guid.Parse("81a00000-0000-0000-0000-000000000001");
        var itworldItemId =
            Guid.Parse("81a00000-0000-0000-0000-000000000002");
        db.Items.AddRange(
            new LocalItem
            {
                Id = usenetItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "USENET rental cache stock guard",
                NameMatchKey = "USENETRENTALCACHESTOCKGUARD",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                IsSale = true,
                CurrentStock = 17m,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddMinutes(-2),
                Revision = 17,
                IsDirty = false
            },
            new LocalItem
            {
                Id = itworldItemId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "ITWORLD rental cache stock guard",
                NameMatchKey = "ITWORLDRENTALCACHESTOCKGUARD",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                IsSale = true,
                CurrentStock = 23m,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddMinutes(-1),
                Revision = 23,
                IsDirty = false
            });
        db.ItemWarehouseStocks.AddRange(
            new LocalItemWarehouseStock
            {
                ItemId = usenetItemId,
                WarehouseCode = DomainConstants.WarehouseUsenetMain,
                Quantity = 17m,
                UpdatedAtUtc = now.AddMinutes(-2),
                Revision = 17
            },
            new LocalItemWarehouseStock
            {
                ItemId = itworldItemId,
                WarehouseCode = DomainConstants.WarehouseItworldMain,
                Quantity = 23m,
                UpdatedAtUtc = now.AddMinutes(-1),
                Revision = 23
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var session = CreateAdminSession();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(
            db,
            new OfficeAccessService(),
            dispatcher,
            session);
        var rental = new RentalStateService(db);
        var diagnostics = new SyncDiagnosticsService(session);
        var handler = new AdministrativeCachePullHandler();
        handler.SetItems(
            "USENET",
            [
                new ItemDto
                {
                    Id = usenetItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal = "USENET rental cache refreshed",
                    NameMatchKey = "USENETRENTALCACHEREFRESHED",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    IsSale = true,
                    CurrentStock = 999m,
                    CreatedAtUtc = now.AddDays(-2),
                    UpdatedAtUtc = now,
                    Revision = 100
                }
            ]);
        handler.SetItems(
            "ITWORLD",
            [
                new ItemDto
                {
                    Id = itworldItemId,
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    NameOriginal = "ITWORLD rental cache refreshed",
                    NameMatchKey = "ITWORLDRENTALCACHEREFRESHED",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    IsSale = true,
                    CurrentStock = 999m,
                    CreatedAtUtc = now.AddDays(-2),
                    UpdatedAtUtc = now,
                    Revision = 200
                }
            ]);
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);
        using var sync = new SyncService(
            db,
            local,
            rental,
            api,
            session,
            dispatcher,
            diagnostics);

        Assert.True(await sync.EnsureAdministrativeBusinessCachesAsync());
        Assert.Equal(
            "100",
            await local.GetSettingAsync(
                "Sync.AdminBusinessCacheRevision.USENET"));
        Assert.Equal(
            "200",
            await local.GetSettingAsync(
                "Sync.AdminBusinessCacheRevision.ITWORLD"));
        Assert.All(
            handler.Requests,
            request => Assert.True(request.RentalAdministrationOnly));

        db.ChangeTracker.Clear();
        var stocks = await db.ItemWarehouseStocks
            .AsNoTracking()
            .OrderBy(stock => stock.ItemId)
            .ToListAsync();
        Assert.Equal(2, stocks.Count);
        Assert.Contains(
            stocks,
            stock =>
                stock.ItemId == usenetItemId &&
                stock.WarehouseCode == DomainConstants.WarehouseUsenetMain &&
                stock.Quantity == 17m);
        Assert.Contains(
            stocks,
            stock =>
                stock.ItemId == itworldItemId &&
                stock.WarehouseCode == DomainConstants.WarehouseItworldMain &&
                stock.Quantity == 23m);

        var items = await db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item =>
                item.Id == usenetItemId ||
                item.Id == itworldItemId)
            .ToDictionaryAsync(item => item.Id);
        Assert.Equal(17m, items[usenetItemId].CurrentStock);
        Assert.Equal(23m, items[itworldItemId].CurrentStock);
        Assert.Equal(
            "USENET rental cache refreshed",
            items[usenetItemId].NameOriginal);
        Assert.Equal(
            "ITWORLD rental cache refreshed",
            items[itworldItemId].NameOriginal);
    }

    [Fact]
    public async Task AdministrativeBusinessCache_RentalOnlyPullAliasRemapPreservesMergedStockTotal()
    {
        await using var connection =
            new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();

        var now = new DateTime(
            2026,
            7,
            31,
            1,
            30,
            0,
            DateTimeKind.Utc);
        var aliasItemId =
            Guid.Parse("81a00000-0000-0000-0000-000000000011");
        var canonicalItemId =
            Guid.Parse("81a00000-0000-0000-0000-000000000012");
        const string materialNumber = "ADMIN-ALIAS-STOCK-001";
        db.Items.AddRange(
            new LocalItem
            {
                Id = aliasItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Admin cache stock alias",
                NameMatchKey = "ADMINCACHESTOCKALIAS",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                MaterialNumber = materialNumber,
                CurrentStock = 4m,
                CreatedAtUtc = now.AddDays(-3),
                UpdatedAtUtc = now.AddMinutes(-3),
                Revision = 4,
                IsDirty = false
            },
            new LocalItem
            {
                Id = canonicalItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Admin cache stock canonical before",
                NameMatchKey = "ADMINCACHESTOCKCANONICALBEFORE",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                Unit = "EA",
                MaterialNumber = materialNumber,
                CurrentStock = 6m,
                CreatedAtUtc = now.AddDays(-2),
                UpdatedAtUtc = now.AddMinutes(-2),
                Revision = 6,
                IsDirty = false
            });
        db.ItemWarehouseStocks.AddRange(
            new LocalItemWarehouseStock
            {
                ItemId = aliasItemId,
                WarehouseCode =
                    DomainConstants.WarehouseUsenetMain,
                Quantity = 4m,
                UpdatedAtUtc = now.AddMinutes(-3),
                Revision = 4
            },
            new LocalItemWarehouseStock
            {
                ItemId = canonicalItemId,
                WarehouseCode =
                    DomainConstants.WarehouseUsenetMain,
                Quantity = 6m,
                UpdatedAtUtc = now.AddMinutes(-2),
                Revision = 6
            });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var session = CreateAdminSession();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(
            db,
            new OfficeAccessService(),
            dispatcher,
            session);
        var handler = new AdministrativeCachePullHandler();
        handler.SetItems(
            "USENET",
            [
                new ItemDto
                {
                    Id = canonicalItemId,
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    NameOriginal =
                        "Admin cache stock canonical refreshed",
                    NameMatchKey =
                        "ADMINCACHESTOCKCANONICALREFRESHED",
                    ItemKind = ItemKinds.Product,
                    TrackingType = ItemTrackingTypes.Stock,
                    Unit = "EA",
                    MaterialNumber = materialNumber,
                    CurrentStock = 999m,
                    CreatedAtUtc = now.AddDays(-2),
                    UpdatedAtUtc = now,
                    Revision = 100
                }
            ]);
        var api = new ErpApiClient(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost/")
            },
            session);
        using var sync = new SyncService(
            db,
            local,
            new RentalStateService(db),
            api,
            session,
            dispatcher,
            new SyncDiagnosticsService(session));

        Assert.True(
            await sync.EnsureAdministrativeBusinessCachesAsync());

        db.ChangeTracker.Clear();
        Assert.False(await db.Items
            .IgnoreQueryFilters()
            .AnyAsync(item => item.Id == aliasItemId));
        var canonical = await db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(item => item.Id == canonicalItemId);
        Assert.Equal(10m, canonical.CurrentStock);
        Assert.Equal(
            "Admin cache stock canonical refreshed",
            canonical.NameOriginal);

        var stock = await db.ItemWarehouseStocks
            .AsNoTracking()
            .SingleAsync();
        Assert.Equal(canonicalItemId, stock.ItemId);
        Assert.Equal(
            DomainConstants.WarehouseUsenetMain,
            stock.WarehouseCode);
        Assert.Equal(10m, stock.Quantity);
        Assert.Equal(
            "100",
            await local.GetSettingAsync(
                "Sync.AdminBusinessCacheRevision.USENET"));
    }

    [Fact]
    public async Task AdministrativeBusinessCache_PartialFailure_RetriesOnlyFailedDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();

        var session = CreateAdminSession();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rental = new RentalStateService(db);
        var diagnostics = new SyncDiagnosticsService(session);
        var handler = new AdministrativeCachePullHandler();
        handler.FailNextRequest("ITWORLD");
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);

        using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);

        Assert.True(await sync.EnsureAdministrativeBusinessCachesAsync());
        Assert.Equal(1, handler.CountRequests("USENET"));
        Assert.Equal(1, handler.CountRequests("ITWORLD"));

        Assert.True(await sync.EnsureAdministrativeBusinessCachesAsync());
        Assert.Equal(1, handler.CountRequests("USENET"));
        Assert.Equal(2, handler.CountRequests("ITWORLD"));

        Assert.False(await sync.EnsureAdministrativeBusinessCachesAsync());
        Assert.Equal(1, handler.CountRequests("USENET"));
        Assert.Equal(2, handler.CountRequests("ITWORLD"));
    }

    [Fact]
    public async Task AdministrativeBusinessCache_UsesIsolatedScopeAndCopiesRefreshMapBackToOwnerService()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-admin-cache-owner-{Guid.NewGuid():N}.db");
        var handler = new AdministrativeCachePullHandler();
        var services = new ServiceCollection();
        services.AddSingleton(CreateAdminSession());
        services.AddSingleton<OfficeAccessService>();
        services.AddSingleton<SyncRequestDispatcher>();
        services.AddSingleton<DesktopDataChangeNotifier>();
        services.AddDbContext<LocalDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Pooling=False"));
        services.AddScoped<LocalStateService>();
        services.AddScoped<RentalStateService>();
        services.AddScoped<SyncDiagnosticsService>();
        services.AddScoped(provider => new ErpApiClient(
            new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost/")
            },
            provider.GetRequiredService<SessionState>()));
        services.AddScoped<SyncService>();

        try
        {
            await using var provider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });

            await using (var initializationScope = provider.CreateAsyncScope())
            {
                var db = initializationScope.ServiceProvider
                    .GetRequiredService<LocalDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            await using var mainScope = provider.CreateAsyncScope();
            var mainProvider = mainScope.ServiceProvider;
            var sync = mainProvider.GetRequiredService<SyncService>();
            var rental = mainProvider.GetRequiredService<RentalStateService>();
            var session = mainProvider.GetRequiredService<SessionState>();
            var mainDb = mainProvider.GetRequiredService<LocalDbContext>();
            var ownerTrackingMarker = new LocalSetting
            {
                Key = $"OwnerTrackingMarker.{Guid.NewGuid():N}",
                Value = "unchanged"
            };
            mainDb.Attach(ownerTrackingMarker);
            Assert.Equal(EntityState.Unchanged, mainDb.Entry(ownerTrackingMarker).State);

            Assert.True(await sync.EnsureAdministrativeBusinessCachesAsync());
            Assert.Equal(2, handler.Requests.Count);
            Assert.All(handler.Requests, request => Assert.True(request.RentalAdministrationOnly));
            Assert.Equal(EntityState.Unchanged, mainDb.Entry(ownerTrackingMarker).State);

            handler.ClearRequests();

            var rows = await rental.GetAssetRowsAsync(new RentalAssetFilter(), session);

            Assert.Empty(rows);
            Assert.Empty(handler.Requests);
            Assert.Same(sync, mainProvider.GetRequiredService<SyncService>());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task SuccessfulSync_WarmsAdministrativeBusinessCachesBeforeRentalScreenOpens()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-admin-cache-warmup-{Guid.NewGuid():N}.db");
        var handler = new PostSyncAdministrativeCacheWarmupHandler();
        var diagnosticOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath};Pooling=False")
            .Options;
        var services = new ServiceCollection();
        services.AddSingleton(CreateAdminSession());
        services.AddSingleton<OfficeAccessService>();
        services.AddSingleton<SyncRequestDispatcher>();
        services.AddSingleton<DesktopDataChangeNotifier>();
        services.AddDbContext<LocalDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Pooling=False"));
        services.AddScoped<LocalStateService>();
        services.AddScoped<RentalStateService>();
        services.AddScoped(provider => new SyncDiagnosticsService(
            provider.GetRequiredService<SessionState>(),
            () => new LocalDbContext(diagnosticOptions)));
        services.AddScoped(provider => new ErpApiClient(
            new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost/")
            },
            provider.GetRequiredService<SessionState>()));
        services.AddScoped<SyncService>();

        try
        {
            await using var provider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });

            await using (var initializationScope = provider.CreateAsyncScope())
            {
                var db = initializationScope.ServiceProvider
                    .GetRequiredService<LocalDbContext>();
                await db.Database.EnsureCreatedAsync();
                db.Settings.Add(new LocalSetting
                {
                    Key = "LastSyncRevision",
                    Value = "1"
                });
                await db.SaveChangesAsync();
            }

            await using var mainScope = provider.CreateAsyncScope();
            var mainProvider = mainScope.ServiceProvider;
            var sync = mainProvider.GetRequiredService<SyncService>();
            var rental = mainProvider.GetRequiredService<RentalStateService>();
            var session = mainProvider.GetRequiredService<SessionState>();
            var local = mainProvider.GetRequiredService<LocalStateService>();

            sync.Start(TimeSpan.FromHours(1));
            var syncSucceeded = await sync.TrySyncAsync()
                .WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(
                syncSucceeded,
                $"normalPulls={handler.NormalPullRequestCount}; lastError={await local.GetSettingAsync("Sync.LastError")}");
            await handler.AdministrativeCachesPrepared.Task.WaitAsync(
                TimeSpan.FromSeconds(15));

            Assert.True(handler.NormalPullRequestCount >= 1);
            Assert.Equal(
                ["ITWORLD", "USENET"],
                handler.AdministrativeDatabaseNames.Order(StringComparer.OrdinalIgnoreCase));
            var administrativeRequestCount = handler.AdministrativePullRequestCount;

            var rows = await rental.GetAssetRowsAsync(
                new RentalAssetFilter(),
                session);

            Assert.Empty(rows);
            Assert.Equal(
                administrativeRequestCount,
                handler.AdministrativePullRequestCount);
            await sync.StopAndDrainAsync().WaitAsync(
                TimeSpan.FromSeconds(15));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task AdministrativeBusinessCache_CancellationDrainsBeforeMainScopeDisposal()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"georaeplan-admin-cache-cancel-{Guid.NewGuid():N}.db");
        var handler = new BlockingAdministrativeCachePullHandler();
        var services = new ServiceCollection();
        services.AddSingleton(CreateAdminSession());
        services.AddSingleton<OfficeAccessService>();
        services.AddSingleton<SyncRequestDispatcher>();
        services.AddSingleton<DesktopDataChangeNotifier>();
        services.AddDbContext<LocalDbContext>(options =>
            options.UseSqlite($"Data Source={databasePath};Pooling=False"));
        services.AddScoped<LocalStateService>();
        services.AddScoped<RentalStateService>();
        services.AddScoped<SyncDiagnosticsService>();
        services.AddScoped(provider => new ErpApiClient(
            new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri("http://localhost/")
            },
            provider.GetRequiredService<SessionState>()));
        services.AddScoped<SyncService>();

        try
        {
            await using var provider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true
                });

            await using (var initializationScope = provider.CreateAsyncScope())
            {
                var db = initializationScope.ServiceProvider
                    .GetRequiredService<LocalDbContext>();
                await db.Database.EnsureCreatedAsync();
            }

            var mainScope = provider.CreateAsyncScope();
            using var lifetimeCts = new CancellationTokenSource();
            try
            {
                var sync = mainScope.ServiceProvider.GetRequiredService<SyncService>();
                var refreshTask = sync.EnsureAdministrativeBusinessCachesAsync(
                    lifetimeCts.Token);

                await handler.PullReceived.Task.WaitAsync(TimeSpan.FromSeconds(15));
                lifetimeCts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => refreshTask.WaitAsync(TimeSpan.FromSeconds(15)));
            }
            finally
            {
                await mainScope.DisposeAsync();
            }

            await using var verificationScope = provider.CreateAsyncScope();
            var verificationDb = verificationScope.ServiceProvider
                .GetRequiredService<LocalDbContext>();
            Assert.False(await verificationDb.Settings
                .AsNoTracking()
                .AnyAsync(setting => setting.Key.StartsWith(
                    "Sync.AdminBusinessCacheRevision.")));
            Assert.Equal(1, handler.RequestCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
            File.Delete(databasePath + "-shm");
            File.Delete(databasePath + "-wal");
        }
    }

    [Fact]
    public async Task AdministrativeBusinessCache_NewSession_RefreshesEveryDatabaseWithStoredRevision()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();

        var session = CreateAdminSession();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rental = new RentalStateService(db);
        var diagnostics = new SyncDiagnosticsService(session);
        var handler = new AdministrativeCachePullHandler();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);
        using var sync = new SyncService(db, local, rental, api, session, dispatcher, diagnostics);

        Assert.True(await sync.EnsureAdministrativeBusinessCachesAsync());
        handler.ClearRequests();

        session.SetSession(
            "admin-cache-token-new-session",
            new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });

        Assert.True(await sync.EnsureAdministrativeBusinessCachesAsync());
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains(handler.Requests, request => request.DatabaseName == "USENET" && request.SinceRevision == 100);
        Assert.Contains(handler.Requests, request => request.DatabaseName == "ITWORLD" && request.SinceRevision == 200);
    }

    private static LocalDbContext CreateDbContext(SqliteConnection connection)
        => new(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options);

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetSession(
            "admin-cache-token",
            new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });
        return session;
    }

    private sealed class BlockingAdministrativeCachePullHandler : HttpMessageHandler
    {
        private int _requestCount;

        public TaskCompletionSource PullReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            PullReceived.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException(
                "취소된 관리자 캐시 요청이 응답 생성까지 진행했습니다.");
        }
    }

    private sealed class PostSyncAdministrativeCacheWarmupHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _administrativeDatabaseNames =
            new(StringComparer.OrdinalIgnoreCase);
        private int _administrativePullRequestCount;
        private int _normalPullRequestCount;

        public TaskCompletionSource<bool> AdministrativeCachesPrepared { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int AdministrativePullRequestCount =>
            Volatile.Read(ref _administrativePullRequestCount);

        public int NormalPullRequestCount =>
            Volatile.Read(ref _normalPullRequestCount);

        public IReadOnlyList<string> AdministrativeDatabaseNames
        {
            get
            {
                lock (_gate)
                    return _administrativeDatabaseNames.ToList();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var databaseName = request.Headers.TryGetValues(
                "X-Tenant-Code",
                out var values)
                ? values.Single()
                : string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;
            var rentalAdministrationOnly = query.Contains(
                "rentalAdministrationOnly=true",
                StringComparison.OrdinalIgnoreCase);

            if (rentalAdministrationOnly)
            {
                Interlocked.Increment(ref _administrativePullRequestCount);
                lock (_gate)
                {
                    _administrativeDatabaseNames.Add(databaseName);
                    if (_administrativeDatabaseNames.Count == 2)
                        AdministrativeCachesPrepared.TrySetResult(true);
                }
            }
            else
            {
                Interlocked.Increment(ref _normalPullRequestCount);
            }

            var currentRevision = string.Equals(
                databaseName,
                "ITWORLD",
                StringComparison.OrdinalIgnoreCase)
                ? 200L
                : 100L;
            var json = JsonSerializer.Serialize(new SyncPullResponse
            {
                CurrentServerRevision = currentRevision
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class AdministrativeCachePullHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _remainingFailures = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IReadOnlyList<ItemDto>>
            _itemsByDatabase = new(StringComparer.OrdinalIgnoreCase);

        public List<PullRequest> Requests { get; } = [];

        public void ClearRequests() => Requests.Clear();

        public int CountRequests(string databaseName)
            => Requests.Count(request => string.Equals(
                request.DatabaseName,
                databaseName,
                StringComparison.OrdinalIgnoreCase));

        public void FailNextRequest(string databaseName)
            => _remainingFailures[databaseName] = 1;

        public void SetItems(
            string databaseName,
            IReadOnlyList<ItemDto> items)
            => _itemsByDatabase[databaseName] = items;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var databaseName = request.Headers.TryGetValues("X-Tenant-Code", out var values)
                ? values.Single()
                : string.Empty;
            var query = request.RequestUri?.Query ?? string.Empty;
            var sinceRevision = ParseLongQueryValue(query, "sinceRev");
            var rentalAdministrationOnly = string.Equals(
                ParseStringQueryValue(query, "rentalAdministrationOnly"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            Requests.Add(new PullRequest(databaseName, sinceRevision, rentalAdministrationOnly));

            if (_remainingFailures.TryGetValue(databaseName, out var remainingFailures) &&
                remainingFailures > 0)
            {
                _remainingFailures[databaseName] = remainingFailures - 1;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            var currentRevision = string.Equals(databaseName, "ITWORLD", StringComparison.OrdinalIgnoreCase)
                ? 200L
                : 100L;
            var json = JsonSerializer.Serialize(new SyncPullResponse
            {
                CurrentServerRevision = currentRevision,
                Items = _itemsByDatabase.TryGetValue(
                    databaseName,
                    out var items)
                    ? items.ToList()
                    : []
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        private static long ParseLongQueryValue(string query, string key)
            => long.TryParse(ParseStringQueryValue(query, key), out var value) ? value : 0L;

        private static string ParseStringQueryValue(string query, string key)
            => query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2 && string.Equals(parts[0], key, StringComparison.OrdinalIgnoreCase))
                .Select(parts => Uri.UnescapeDataString(parts[1]))
                .FirstOrDefault() ?? string.Empty;
    }

    private sealed record PullRequest(
        string DatabaseName,
        long SinceRevision,
        bool RentalAdministrationOnly);
}
