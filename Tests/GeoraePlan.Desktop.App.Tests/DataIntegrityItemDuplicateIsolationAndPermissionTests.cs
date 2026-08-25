using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class DataIntegrityItemDuplicateIsolationAndPermissionTests
{
    [Fact]
    public async Task MergeDuplicateItemIssueAsync_RuntimeGateDefersConcurrentParentWriteThenRollbackLeavesParentReusable()
    {
        PrepareAppRoot("georaeplan-integrity-isolated-child-rollback");

        try
        {
            await using var provider = CreateProductionServiceProvider();
            await using var parentScope = provider.CreateAsyncScope();
            var parentDb = parentScope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await InitializeDatabaseAsync(parentDb, useWal: true);

            var canonical = CreateItem("80111111-1111-1111-1111-111111111111", "격리 롤백 품목", OfficeCodeCatalog.Usenet);
            var duplicate = CreateItem("80222222-2222-2222-2222-222222222222", "격리 롤백 품목", OfficeCodeCatalog.Usenet);
            parentDb.Items.AddRange(canonical, duplicate);
            await parentDb.SaveChangesAsync();

            var service = parentScope.ServiceProvider.GetRequiredService<DataIntegrityIssueService>();
            var session = CreateAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            Assert.True(comparison.CanMerge, comparison.BlockingReasonText);

            var childReachedSaveGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseChild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var isolatedChildUsesDifferentDbContext = false;
            service.TestOnlyConfigureIsolatedChild = child =>
            {
                var childDb = Assert.IsType<LocalDbContext>(typeof(DataIntegrityIssueService)
                    .GetField("_db", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(child));
                isolatedChildUsesDifferentDbContext = !ReferenceEquals(parentDb, childDb);
                child.TestOnlyBeforeDuplicateMergeSaveAsync = async _ =>
                {
                    childReachedSaveGate.TrySetResult();
                    await releaseChild.Task;
                    throw new InvalidOperationException("isolated-child-merge-fault");
                };
            };

            var mergeTask = service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Task<int>? parentSaveTask = null;
            try
            {
                await childReachedSaveGate.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(isolatedChildUsesDifferentDbContext);

                parentDb.AuditLogs.Add(CreateProofAudit("DeferredParentWriteDuringChildTransaction"));
                parentSaveTask = parentDb.SaveChangesAsync();
                await Task.Delay(150);
                Assert.False(parentSaveTask.IsCompleted);
            }
            finally
            {
                releaseChild.TrySetResult();
            }

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await mergeTask.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal("isolated-child-merge-fault", exception.Message);
            Assert.NotNull(parentSaveTask);
            Assert.Equal(1, await parentSaveTask!.WaitAsync(TimeSpan.FromSeconds(5)));

            Assert.True(await parentDb.AuditLogs.AsNoTracking()
                .AnyAsync(log => log.Action == "DeferredParentWriteDuringChildTransaction"));
            var storedItems = await parentDb.Items.IgnoreQueryFilters().AsNoTracking()
                .Where(item => item.Id == canonical.Id || item.Id == duplicate.Id)
                .ToDictionaryAsync(item => item.Id);
            Assert.False(storedItems[canonical.Id].IsDeleted);
            Assert.False(storedItems[duplicate.Id].IsDeleted);

            parentDb.AuditLogs.Add(CreateProofAudit("ParentSaveAfterChildRollback"));
            await parentDb.SaveChangesAsync();
            Assert.True(await parentDb.AuditLogs.AsNoTracking()
                .AnyAsync(log => log.Action == "ParentSaveAfterChildRollback"));

            parentDb.AuditLogs.Add(CreateProofAudit("ParentSecondSaveAfterChildRollback"));
            await parentDb.SaveChangesAsync();
            Assert.True(await parentDb.AuditLogs.AsNoTracking()
                .AnyAsync(log => log.Action == "ParentSecondSaveAfterChildRollback"));
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_IsolatedChildCommitDetachesParentUnchangedCandidatesAndFreshQuerySeesCommit()
    {
        PrepareAppRoot("georaeplan-integrity-isolated-child-commit");

        try
        {
            await using var provider = CreateProductionServiceProvider();
            await using var parentScope = provider.CreateAsyncScope();
            var parentDb = parentScope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await InitializeDatabaseAsync(parentDb);

            var canonical = CreateItem("81111111-1111-1111-1111-111111111111", "격리 성공 품목", OfficeCodeCatalog.Usenet);
            var duplicate = CreateItem("81222222-2222-2222-2222-222222222222", "격리 성공 품목", OfficeCodeCatalog.Usenet);
            parentDb.Items.AddRange(canonical, duplicate);
            await parentDb.SaveChangesAsync();
            Assert.Equal(EntityState.Unchanged, parentDb.Entry(canonical).State);
            Assert.Equal(EntityState.Unchanged, parentDb.Entry(duplicate).State);

            var service = parentScope.ServiceProvider.GetRequiredService<DataIntegrityIssueService>();
            var session = CreateAdminSession();
            var issue = Assert.Single(
                (await service.ScanAsync(session)).Issues,
                current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                canonical.Id,
                comparison.SnapshotToken,
                session);

            Assert.True(result.Success, result.Message);
            Assert.Equal(EntityState.Detached, parentDb.Entry(canonical).State);
            Assert.Equal(EntityState.Detached, parentDb.Entry(duplicate).State);

            var freshCanonical = await parentDb.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == canonical.Id);
            var freshDuplicate = await parentDb.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == duplicate.Id);
            Assert.NotSame(canonical, freshCanonical);
            Assert.NotSame(duplicate, freshDuplicate);
            Assert.False(freshCanonical.IsDeleted);
            Assert.True(freshDuplicate.IsDeleted);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Fact]
    public async Task PrepareItemDuplicateReviewAsync_ItemEditWithoutDeliveryEditShowsTransferPermissionBlock()
    {
        PrepareAppRoot("georaeplan-integrity-transfer-review-permission");

        try
        {
            await using var provider = CreateProductionServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await InitializeDatabaseAsync(db);
            var fixture = await SeedDuplicateTransferAsync(db, OfficeCodeCatalog.Usenet, "821");
            var service = scope.ServiceProvider.GetRequiredService<DataIntegrityIssueService>();
            var issue = await GetItemDuplicateIssueAsync(service, CreateAdminSession());
            var itemOnlySession = CreateUserSession(
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeTenantAll,
                AppPermissionNames.ItemEdit);

            var preparation = await service.PrepareItemDuplicateReviewAsync(issue, itemOnlySession);

            Assert.True(preparation.Comparison.CanMerge, preparation.Comparison.BlockingReasonText);
            Assert.False(preparation.CanMerge);
            Assert.Contains("재고이동 편집 권한", preparation.BlockingReasonText, StringComparison.Ordinal);
            Assert.Contains(preparation.PermissionBlockingReasons, reason =>
                reason.Contains("재고이동 편집 권한", StringComparison.Ordinal));
            Assert.Equal(fixture.Duplicate.Id, (await db.InventoryTransferLines.AsNoTracking()
                .SingleAsync(line => line.TransferId == fixture.TransferId)).ItemId);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Fact]
    public async Task MergeDuplicateItemIssueAsync_ItemEditWithoutDeliveryEditFailsClosedWithZeroWrites()
    {
        PrepareAppRoot("georaeplan-integrity-transfer-merge-permission");

        try
        {
            await using var provider = CreateProductionServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await InitializeDatabaseAsync(db);
            var fixture = await SeedDuplicateTransferAsync(db, OfficeCodeCatalog.Usenet, "831");
            var service = scope.ServiceProvider.GetRequiredService<DataIntegrityIssueService>();
            var issue = await GetItemDuplicateIssueAsync(service, CreateAdminSession());
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var itemOnlySession = CreateUserSession(
                OfficeCodeCatalog.Usenet,
                TenantScopeCatalog.ScopeTenantAll,
                AppPermissionNames.ItemEdit);

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                fixture.Canonical.Id,
                comparison.SnapshotToken,
                itemOnlySession);

            Assert.False(result.Success);
            Assert.Contains("재고이동 편집 권한", result.Message, StringComparison.Ordinal);
            await AssertTransferFixtureUnchangedAsync(db, fixture);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    [Theory]
    [InlineData(OfficeCodeCatalog.Usenet)]
    [InlineData(OfficeCodeCatalog.Yeonsu)]
    public async Task ItemDuplicateTransferMerge_OfficeOnlySourceOrTargetUserRequiresBothTransferEndpoints(string sessionOfficeCode)
    {
        PrepareAppRoot($"georaeplan-integrity-transfer-endpoints-{sessionOfficeCode.ToLowerInvariant()}");

        try
        {
            await using var provider = CreateProductionServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<LocalDbContext>();
            await InitializeDatabaseAsync(db);
            var fixture = await SeedDuplicateTransferAsync(db, sessionOfficeCode, sessionOfficeCode == OfficeCodeCatalog.Usenet ? "841" : "842");
            var service = scope.ServiceProvider.GetRequiredService<DataIntegrityIssueService>();
            var issue = await GetItemDuplicateIssueAsync(service, CreateAdminSession(sessionOfficeCode));
            var comparison = Assert.IsType<DataIntegrityItemDuplicateComparison>(issue.ItemDuplicateComparison);
            var officeOnlySession = CreateUserSession(
                sessionOfficeCode,
                TenantScopeCatalog.ScopeOfficeOnly,
                AppPermissionNames.ItemEdit,
                AppPermissionNames.DeliveryEdit);

            var preparation = await service.PrepareItemDuplicateReviewAsync(issue, officeOnlySession);
            Assert.False(preparation.CanMerge);
            Assert.Contains("출발·도착 사업장", preparation.BlockingReasonText, StringComparison.Ordinal);

            var result = await service.MergeDuplicateItemIssueAsync(
                issue,
                fixture.Canonical.Id,
                comparison.SnapshotToken,
                officeOnlySession);
            Assert.False(result.Success);
            Assert.Contains("출발·도착 사업장", result.Message, StringComparison.Ordinal);
            await AssertTransferFixtureUnchangedAsync(db, fixture);
        }
        finally
        {
            ResetAppRoot();
        }
    }

    private static ServiceProvider CreateProductionServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddSingleton<SessionState>();
        services.AddSingleton<OfficeAccessService>();
        services.AddSingleton<SyncRequestDispatcher>();
        services.AddSingleton<DesktopDataChangeNotifier>();
        services.AddDbContext<LocalDbContext>();
        services.AddScoped<LocalStateService>();
        services.AddScoped(provider =>
        {
            var service = new DataIntegrityIssueService(
                provider.GetRequiredService<LocalDbContext>(),
                provider.GetRequiredService<SyncRequestDispatcher>(),
                provider.GetRequiredService<LocalStateService>(),
                provider.GetRequiredService<IServiceScopeFactory>());
            service.TestOnlyUseLegacyLocalItemDuplicateMerge = true;
            return service;
        });
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private static async Task InitializeDatabaseAsync(LocalDbContext db, bool useWal = false)
    {
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
        if (!useWal)
            return;

        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";
            Assert.Equal("wal", Convert.ToString(await command.ExecuteScalarAsync())?.ToLowerInvariant());
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<TransferFixture> SeedDuplicateTransferAsync(
        LocalDbContext db,
        string itemOfficeCode,
        string idPrefix)
    {
        var canonical = CreateItem($"{idPrefix}11111-1111-1111-1111-111111111111", "재고이동 권한 품목", itemOfficeCode);
        var duplicate = CreateItem($"{idPrefix}22222-2222-2222-2222-222222222222", "재고이동 권한 품목", itemOfficeCode);
        var transferId = Guid.Parse($"{idPrefix}33333-3333-3333-3333-333333333333");
        db.Items.AddRange(canonical, duplicate);
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            FromWarehouseCode = DomainConstants.WarehouseUsenetMain,
            ToWarehouseCode = DomainConstants.WarehouseYeonsuMain,
            TransferNumber = $"TRANSFER-{idPrefix}",
            IsDirty = false,
            Lines =
            {
                new LocalInventoryTransferLine
                {
                    TransferId = transferId,
                    ItemId = duplicate.Id,
                    ItemNameOriginal = duplicate.NameOriginal,
                    SpecificationOriginal = duplicate.SpecificationOriginal,
                    Quantity = 1m
                }
            }
        });
        await db.SaveChangesAsync();
        return new TransferFixture(canonical, duplicate, transferId);
    }

    private static async Task<DataIntegrityIssueDetail> GetItemDuplicateIssueAsync(
        DataIntegrityIssueService service,
        SessionState session)
        => Assert.Single(
            (await service.ScanAsync(session)).Issues,
            current => current.Code == DataIntegrityIssueCodes.ItemDuplicateCandidate);

    private static async Task AssertTransferFixtureUnchangedAsync(LocalDbContext db, TransferFixture fixture)
    {
        var items = await db.Items.IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.Id == fixture.Canonical.Id || item.Id == fixture.Duplicate.Id)
            .ToDictionaryAsync(item => item.Id);
        Assert.False(items[fixture.Canonical.Id].IsDeleted);
        Assert.False(items[fixture.Canonical.Id].IsDirty);
        Assert.False(items[fixture.Duplicate.Id].IsDeleted);
        Assert.False(items[fixture.Duplicate.Id].IsDirty);

        var line = await db.InventoryTransferLines.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(current => current.TransferId == fixture.TransferId);
        Assert.Equal(fixture.Duplicate.Id, line.ItemId);
        var transfer = await db.InventoryTransfers.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(current => current.Id == fixture.TransferId);
        Assert.False(transfer.IsDirty);
        Assert.Empty(await db.AuditLogs.AsNoTracking()
            .Where(log => log.Action == "DataIntegrityDuplicateMerge")
            .ToListAsync());
    }

    private static LocalItem CreateItem(string id, string name, string officeCode)
        => new()
        {
            Id = Guid.Parse(id),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode,
            NameOriginal = name,
            NameMatchKey = name,
            SpecificationOriginal = "동일규격",
            SpecificationMatchKey = "동일규격",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 0m,
            IsDirty = false
        };

    private static LocalAuditLog CreateProofAudit(string action)
        => new()
        {
            EntityName = "IsolationProof",
            EntityId = Guid.NewGuid().ToString("D"),
            Action = action,
            CreatedAtUtc = DateTime.UtcNow
        };

    private static SessionState CreateAdminSession(string officeCode = OfficeCodeCatalog.Usenet)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }

    private static SessionState CreateUserSession(
        string officeCode,
        string scopeType,
        params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "user",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = officeCode,
            ScopeType = scopeType,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var appRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(appRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", appRoot);
    }

    private static void ResetAppRoot()
    {
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
        SqliteConnection.ClearAllPools();
    }

    private sealed record TransferFixture(LocalItem Canonical, LocalItem Duplicate, Guid TransferId);
}
