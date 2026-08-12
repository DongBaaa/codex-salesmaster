using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class SyncRentalReferencePermissionTests
{
    [Fact]
    public async Task FlushPendingChangesAsync_GlobalAdmin_SendsRentalItemAndAssetToTheirBusinessDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await EnsureDiagnosticsDatabaseAsync();

        var now = DateTime.UtcNow;
        var usenetItemId = Guid.NewGuid();
        var itworldRentalItemId = Guid.NewGuid();
        var itworldStockItemId = Guid.NewGuid();
        var itworldCustomerId = Guid.NewGuid();
        var itworldHistoryCustomerId = Guid.NewGuid();
        var itworldProfileId = Guid.NewGuid();
        var itworldAssetId = Guid.NewGuid();
        var itworldAssignmentId = Guid.NewGuid();
        var itworldBillingLogId = Guid.NewGuid();
        var usenetPriceGradeId = Guid.NewGuid();
        var itworldPriceGradeId = Guid.NewGuid();
        var priceGradeOptionId = Guid.NewGuid();
        db.Items.AddRange(
            new LocalItem
            {
                Id = usenetItemId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "USENET 일반 품목",
                NameMatchKey = "USENET 일반 품목",
                Revision = 1,
                IsDirty = true,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            },
            new LocalItem
            {
                Id = itworldRentalItemId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "ITWORLD 렌탈 자동생성 품목",
                NameMatchKey = "ITWORLD 렌탈 자동생성 품목",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Asset,
                IsRental = true,
                Revision = 1,
                IsDirty = true,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            },
            new LocalItem
            {
                Id = itworldStockItemId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "ITWORLD 재고 품목",
                NameMatchKey = "ITWORLD 재고 품목",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                CurrentStock = 7m,
                Revision = 1,
                IsDirty = true,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            });
        db.PriceGradeOptions.Add(new LocalPriceGradeOption
        {
            Id = priceGradeOptionId,
            Name = "ITWORLD 테스트 등급",
            IsActive = true,
            IsDirty = true,
            Revision = 1,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now.AddDays(-1)
        });
        db.ItemPriceGrades.AddRange(
            new LocalItemPriceGrade
            {
                Id = usenetPriceGradeId,
                ItemId = usenetItemId,
                PriceGradeOptionId = priceGradeOptionId,
                PriceGradeName = "SHARED-GRADE",
                UnitPrice = 111_000m,
                IsActive = true,
                IsDirty = true,
                Revision = 1,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            },
            new LocalItemPriceGrade
            {
            Id = itworldPriceGradeId,
            ItemId = itworldStockItemId,
            PriceGradeOptionId = priceGradeOptionId,
            PriceGradeName = "ITWORLD 테스트 등급",
            UnitPrice = 123_000m,
            IsActive = true,
            IsDirty = true,
            Revision = 1,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
            });
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = itworldStockItemId,
            WarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
            Quantity = 7m,
            Revision = 1,
            UpdatedAtUtc = now
            });
        db.Customers.AddRange(
            new LocalCustomer
            {
                Id = itworldCustomerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "ITWORLD dirty customer",
                NameMatchKey = "ITWORLD DIRTY CUSTOMER",
                IsDirty = true,
                Revision = 1,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            },
            new LocalCustomer
            {
                Id = itworldHistoryCustomerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "ITWORLD reference-only history customer",
                NameMatchKey = "ITWORLD REFERENCE-ONLY HISTORY CUSTOMER",
                IsDirty = false,
                Revision = 5,
                CreatedAtUtc = now.AddDays(-5),
                UpdatedAtUtc = now.AddDays(-2)
            });
        db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
        {
            Id = itworldProfileId,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            ManagementCompanyCode = OfficeCodeCatalog.Itworld,
            CustomerId = itworldCustomerId,
            ProfileKey = $"ITWORLD-PROFILE-{itworldProfileId:N}",
            CustomerName = "ITWORLD rental customer",
            Revision = 1,
            IsDirty = true,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        db.RentalAssets.Add(new LocalRentalAsset
        {
            Id = itworldAssetId,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            ManagementCompanyCode = OfficeCodeCatalog.Itworld,
            CustomerId = itworldCustomerId,
            AssetKey = $"ITWORLD-AUTO-{itworldAssetId:N}",
            ItemId = itworldRentalItemId,
            BillingProfileId = itworldProfileId,
            ItemName = "ITWORLD 렌탈 자동생성 품목",
            Revision = 1,
            IsDirty = true,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
        {
            Id = itworldAssignmentId,
            AssetId = itworldAssetId,
            BillingProfileId = itworldProfileId,
            CustomerId = itworldHistoryCustomerId,
            TenantCode = TenantScopeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            CustomerName = "ITWORLD rental customer",
            Revision = 1,
            IsDirty = true,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        db.RentalBillingLogs.Add(new LocalRentalBillingLog
        {
            Id = itworldBillingLogId,
            BillingProfileId = itworldProfileId,
            TenantCode = TenantScopeCatalog.Itworld,
            OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
            BillingYearMonth = "2026-07",
            ScheduledDate = new DateOnly(2026, 7, 25),
            Revision = 1,
            IsDirty = true,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();

        var session = new SessionState();
        session.SetSession("global-admin-token", new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "global-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });

        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        await localState.SetSettingAsync("LastSyncRevision", "1");
        var rentalState = new RentalStateService(db);
        var handler = new CaptureBusinessDatabasePushHandler();
        handler.FailNextPush("ITWORLD");
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);
        var diagnostics = new SyncDiagnosticsService(session);
        using var sync = new SyncService(db, localState, rentalState, api, session, dispatcher, diagnostics);

        Assert.False(await sync.FlushPendingChangesAsync());
        Assert.True(await localState.HasPendingSyncChangesAsync(session));
        Assert.Single(await db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry =>
                entry.EntityName == nameof(LocalItem) &&
                entry.EntityId == itworldStockItemId &&
                entry.Status == "Failed")
            .ToListAsync());

        db.ChangeTracker.Clear();
        var modifiedStockItem = await db.Items
            .IgnoreQueryFilters()
            .SingleAsync(item => item.Id == itworldStockItemId);
        modifiedStockItem.Notes = "실패 후 수정된 최신 전체 스냅샷";
        modifiedStockItem.UpdatedAtUtc = now.AddMinutes(5);
        modifiedStockItem.IsDirty = true;
        await db.SaveChangesAsync();

        var secondFlushSucceeded = await sync.FlushPendingChangesAsync();
        if (!secondFlushSucceeded)
        {
            var dirtyItems = await db.Items
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(item => item.IsDirty)
                .Select(item => item.Id)
                .ToListAsync();
            var dirtyPriceGrades = await db.ItemPriceGrades
                .AsNoTracking()
                .Where(priceGrade => priceGrade.IsDirty)
                .Select(priceGrade => priceGrade.Id)
                .ToListAsync();
            var dirtyOptions = await db.PriceGradeOptions
                .AsNoTracking()
                .Where(option => option.IsDirty)
                .Select(option => option.Id)
                .ToListAsync();
            var dirtyAssets = await db.RentalAssets
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(asset => asset.IsDirty)
                .Select(asset => asset.Id)
                .ToListAsync();
            var outbox = await db.SyncOutboxEntries
                .AsNoTracking()
                .OrderBy(entry => entry.PreparedAtUtc)
                .Select(entry => $"{entry.EntityName}/{entry.EntityId:N}/{entry.BusinessDatabaseName}/{entry.Status}/{entry.MutationId}")
                .ToListAsync();
            var lastError = await localState.GetSettingAsync("Sync.LastError");

            Assert.True(
                secondFlushSucceeded,
                $"LastError={lastError}; DirtyItems={string.Join(',', dirtyItems)}; " +
                $"DirtyPriceGrades={string.Join(',', dirtyPriceGrades)}; DirtyOptions={string.Join(',', dirtyOptions)}; " +
                $"DirtyAssets={string.Join(',', dirtyAssets)}; Outbox={string.Join('|', outbox)}");
        }
        Assert.False(await localState.HasPendingSyncChangesAsync(session));

        var usenetPush = Assert.Single(
            handler.PushRequests,
            captured => string.Equals(captured.BusinessDatabaseName, "USENET", StringComparison.OrdinalIgnoreCase));
        var itworldPushes = handler.PushRequests
            .Where(captured => string.Equals(captured.BusinessDatabaseName, "ITWORLD", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(2, itworldPushes.Count);
        var itworldPush = itworldPushes[^1];

        Assert.Contains(usenetPush.Request.Items, item => item.Id == usenetItemId);
        Assert.Equal(usenetPriceGradeId, Assert.Single(usenetPush.Request.ItemPriceGrades).Id);
        Assert.Equal(priceGradeOptionId, Assert.Single(usenetPush.Request.PriceGradeOptions).Id);
        Assert.DoesNotContain(usenetPush.Request.Items, item => item.Id == itworldRentalItemId);
        Assert.DoesNotContain(usenetPush.Request.Items, item => item.Id == itworldStockItemId);
        Assert.DoesNotContain(usenetPush.Request.ItemPriceGrades, price => price.Id == itworldPriceGradeId);
        Assert.DoesNotContain(usenetPush.Request.ItemWarehouseStocks, stock => stock.ItemId == itworldStockItemId);
        Assert.DoesNotContain(usenetPush.Request.Customers, customer => customer.Id == itworldCustomerId);
        Assert.DoesNotContain(usenetPush.Request.Customers, customer => customer.Id == itworldHistoryCustomerId);
        Assert.DoesNotContain(usenetPush.Request.RentalAssets, asset => asset.Id == itworldAssetId);

        Assert.Equal(
            new[] { itworldCustomerId, itworldHistoryCustomerId }.OrderBy(id => id).ToArray(),
            itworldPush.Request.Customers
                .Select(customer => customer.Id)
                .OrderBy(id => id)
                .ToArray());
        Assert.Contains(itworldPush.Request.Items, item => item.Id == itworldRentalItemId);
        Assert.Contains(itworldPush.Request.Items, item => item.Id == itworldStockItemId);
        Assert.Equal(itworldProfileId, Assert.Single(itworldPush.Request.RentalBillingProfiles).Id);
        Assert.Equal(itworldAssetId, Assert.Single(itworldPush.Request.RentalAssets).Id);
        Assert.Equal(itworldAssignmentId, Assert.Single(itworldPush.Request.RentalAssetAssignmentHistories).Id);
        Assert.Equal(itworldBillingLogId, Assert.Single(itworldPush.Request.RentalBillingLogs).Id);
        Assert.Equal(itworldStockItemId, Assert.Single(itworldPush.Request.ItemPriceGrades).ItemId);
        Assert.All(
            itworldPushes,
            captured => Assert.Equal(priceGradeOptionId, Assert.Single(captured.Request.PriceGradeOptions).Id));
        Assert.Equal(itworldStockItemId, Assert.Single(itworldPush.Request.ItemWarehouseStocks).ItemId);

        var stockItemOutbox = await db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry => entry.EntityName == nameof(LocalItem) && entry.EntityId == itworldStockItemId)
            .OrderBy(entry => entry.PreparedAtUtc)
            .ToListAsync();
        Assert.Equal(2, stockItemOutbox.Count);
        Assert.All(stockItemOutbox, entry => Assert.Equal("Acknowledged", entry.Status));

        var optionOutbox = await db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry => entry.EntityName == nameof(LocalPriceGradeOption) && entry.EntityId == priceGradeOptionId)
            .SingleAsync();
        Assert.Equal("Acknowledged", optionOutbox.Status);
        Assert.Equal("USENET", optionOutbox.BusinessDatabaseName);
        Assert.False((await db.PriceGradeOptions.AsNoTracking().SingleAsync()).IsDirty);
        var dependencyOutbox = await db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry => entry.EntityId == itworldHistoryCustomerId)
            .Select(entry => $"{entry.EntityName}/{entry.EntityId:N}/{entry.BusinessDatabaseName}/{entry.Status}")
            .ToListAsync();
        Assert.True(
            dependencyOutbox.Count == 0,
            $"Reference-only customer created outbox rows: {string.Join('|', dependencyOutbox)}");
        var storedReferenceCustomer = await db.Customers
            .AsNoTracking()
            .SingleAsync(customer => customer.Id == itworldHistoryCustomerId);
        Assert.False(storedReferenceCustomer.IsDirty);
        Assert.Equal(5, storedReferenceCustomer.Revision);
        var dirtyCustomerOutbox = await db.SyncOutboxEntries
            .AsNoTracking()
            .Where(entry =>
                entry.EntityName == nameof(LocalCustomer) &&
                entry.EntityId == itworldCustomerId)
            .ToListAsync();
        Assert.NotEmpty(dirtyCustomerOutbox);
        Assert.All(dirtyCustomerOutbox, entry =>
        {
            Assert.Equal("ITWORLD", entry.BusinessDatabaseName);
            Assert.Equal("Acknowledged", entry.Status);
        });
    }

    [Fact]
    public async Task FlushPendingChangesAsync_GlobalAdmin_CanonicalPrimaryDependencyConflict_ContinuesSupplementalPush()
    {
        await using var connection =
            new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await EnsureDiagnosticsDatabaseAsync();

        var now = DateTime.UtcNow;
        var managementCompanyId = Guid.NewGuid();
        var canonicalServerCompanyId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var itworldItemId = Guid.NewGuid();
        db.RentalManagementCompanies.Add(
            new LocalRentalManagementCompany
            {
                Id = managementCompanyId,
                Code = OfficeCodeCatalog.Usenet,
                Name = OfficeCodeCatalog.Usenet,
                IsSystemDefault = true,
                IsActive = true,
                Revision = 7,
                IsDirty = false,
                CreatedAtUtc = now.AddDays(-5),
                UpdatedAtUtc = now.AddDays(-2)
            });
        db.RentalBillingProfiles.Add(
            new LocalRentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"PRIMARY-PROFILE-{profileId:N}",
                CustomerName = "PRIMARY RENTAL CUSTOMER",
                Revision = 1,
                IsDirty = true,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            });
        db.Items.Add(
            new LocalItem
            {
                Id = itworldItemId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "ITWORLD SUPPLEMENTAL ITEM",
                NameMatchKey = "ITWORLD SUPPLEMENTAL ITEM",
                ItemKind = ItemKinds.Product,
                TrackingType = ItemTrackingTypes.Stock,
                Revision = 1,
                IsDirty = true,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            });
        await db.SaveChangesAsync();

        var session = new SessionState();
        session.SetSession(
            "global-admin-token",
            new UserSessionDto
            {
                UserId = Guid.NewGuid(),
                Username = "global-admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });
        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(
            db,
            new OfficeAccessService(),
            dispatcher,
            session);
        await localState.SetSettingAsync("LastSyncRevision", "1");
        var handler =
            new CanonicalPrimaryDependencyConflictHandler(
                canonicalServerCompanyId);
        var api = new ErpApiClient(
            new HttpClient(handler)
            {
                BaseAddress = new Uri("http://localhost/")
            },
            session);
        using var sync = new SyncService(
            db,
            localState,
            new RentalStateService(db),
            api,
            session,
            dispatcher,
            new SyncDiagnosticsService(session));

        Assert.True(await sync.FlushPendingChangesAsync());
        Assert.Equal(2, handler.PushRequests.Count);
        var primary = Assert.Single(
            handler.PushRequests,
            push => string.Equals(
                push.BusinessDatabaseName,
                "USENET",
                StringComparison.OrdinalIgnoreCase));
        var supplemental = Assert.Single(
            handler.PushRequests,
            push => string.Equals(
                push.BusinessDatabaseName,
                "ITWORLD",
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            managementCompanyId,
            Assert.Single(
                primary.Request.RentalManagementCompanies).Id);
        Assert.Equal(
            profileId,
            Assert.Single(primary.Request.RentalBillingProfiles).Id);
        Assert.Equal(
            itworldItemId,
            Assert.Single(supplemental.Request.Items).Id);

        db.ChangeTracker.Clear();
        Assert.False(
            await db.RentalManagementCompanies
                .AsNoTracking()
                .Where(company => company.Id == managementCompanyId)
                .Select(company => company.IsDirty)
                .SingleAsync());
        Assert.False(
            await db.RentalBillingProfiles
                .AsNoTracking()
                .Where(profile => profile.Id == profileId)
                .Select(profile => profile.IsDirty)
                .SingleAsync());
        Assert.False(
            await db.Items
                .AsNoTracking()
                .Where(item => item.Id == itworldItemId)
                .Select(item => item.IsDirty)
                .SingleAsync());
        Assert.False(
            await db.SyncOutboxEntries
                .AsNoTracking()
                .AnyAsync(entry =>
                    entry.EntityName ==
                    nameof(LocalRentalManagementCompany) &&
                    entry.EntityId == managementCompanyId));
        Assert.Equal(
            "Acknowledged",
            await db.SyncOutboxEntries
                .AsNoTracking()
                .Where(entry =>
                    entry.EntityName == nameof(LocalItem) &&
                    entry.EntityId == itworldItemId)
                .Select(entry => entry.Status)
                .SingleAsync());
    }

    [Fact]
    public async Task FlushPendingChangesAsync_GlobalAdmin_DoesNotLeakOrphanItemDependentsIntoCurrentDatabase()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await EnsureDiagnosticsDatabaseAsync();

        var now = DateTime.UtcNow;
        var missingItemId = Guid.NewGuid();
        var optionId = Guid.NewGuid();
        var gradeId = Guid.NewGuid();
        db.PriceGradeOptions.Add(new LocalPriceGradeOption
        {
            Id = optionId,
            Name = "ORPHAN-GRADE",
            IsActive = true,
            Revision = 1,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now.AddDays(-1)
        });
        db.ItemPriceGrades.Add(new LocalItemPriceGrade
        {
            Id = gradeId,
            ItemId = missingItemId,
            PriceGradeOptionId = optionId,
            PriceGradeName = "ORPHAN-GRADE",
            UnitPrice = 10_000m,
            IsActive = true,
            IsDirty = true,
            Revision = 1,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = missingItemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 9m,
            Revision = 1,
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();

        var session = new SessionState();
        session.SetSession("global-admin-token", new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "global-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });

        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        await localState.SetSettingAsync("LastSyncRevision", "1");
        var handler = new CaptureBusinessDatabasePushHandler();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);
        using var sync = new SyncService(
            db,
            localState,
            new RentalStateService(db),
            api,
            session,
            dispatcher,
            new SyncDiagnosticsService(session));

        Assert.True(await sync.FlushPendingChangesAsync());

        var currentDatabasePush = Assert.Single(handler.PushRequests);
        Assert.Equal("USENET", currentDatabasePush.BusinessDatabaseName, ignoreCase: true);
        Assert.Empty(currentDatabasePush.Request.Items);
        Assert.Empty(currentDatabasePush.Request.ItemPriceGrades);
        Assert.Empty(currentDatabasePush.Request.ItemWarehouseStocks);
        Assert.True((await db.ItemPriceGrades.AsNoTracking().SingleAsync(row => row.Id == gradeId)).IsDirty);
        Assert.True(await db.ItemWarehouseStocks.AsNoTracking().AnyAsync(stock => stock.ItemId == missingItemId));
        Assert.True(await localState.HasPendingSyncChangesAsync());
        Assert.False(await db.SyncOutboxEntries.AsNoTracking().AnyAsync(entry => entry.EntityId == gradeId));
    }

    [Fact]
    public async Task FlushPendingChangesAsync_GlobalAdmin_CurrentDatabaseReferenceOnlyPriceGradeOption_DoesNotChangeLocalState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await EnsureDiagnosticsDatabaseAsync();

        var now = DateTime.UtcNow;
        var itemId = Guid.NewGuid();
        var optionId = Guid.NewGuid();
        var gradeId = Guid.NewGuid();
        var optionUpdatedAtUtc = now.AddDays(-2);
        db.Items.Add(new LocalItem
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "USENET CLEAN OPTION ITEM",
            NameMatchKey = "USENET CLEAN OPTION ITEM",
            Revision = 4,
            IsDirty = false,
            CreatedAtUtc = now.AddDays(-3),
            UpdatedAtUtc = now.AddDays(-2)
        });
        db.PriceGradeOptions.Add(new LocalPriceGradeOption
        {
            Id = optionId,
            Name = "REFERENCE-ONLY-GRADE",
            IsActive = true,
            IsDirty = false,
            Revision = 7,
            CreatedAtUtc = now.AddDays(-3),
            UpdatedAtUtc = optionUpdatedAtUtc
        });
        db.ItemPriceGrades.Add(new LocalItemPriceGrade
        {
            Id = gradeId,
            ItemId = itemId,
            PriceGradeOptionId = optionId,
            PriceGradeName = "REFERENCE-ONLY-GRADE",
            UnitPrice = 321_000m,
            IsActive = true,
            IsDirty = true,
            Revision = 1,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();

        var session = new SessionState();
        session.SetSession("global-admin-token", new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "global-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });

        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        await localState.SetSettingAsync("LastSyncRevision", "1");
        var handler = new CaptureBusinessDatabasePushHandler();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);
        using var sync = new SyncService(
            db,
            localState,
            new RentalStateService(db),
            api,
            session,
            dispatcher,
            new SyncDiagnosticsService(session));

        Assert.True(await sync.FlushPendingChangesAsync());

        var push = Assert.Single(handler.PushRequests);
        Assert.Equal("USENET", push.BusinessDatabaseName, ignoreCase: true);
        Assert.Equal(optionId, Assert.Single(push.Request.PriceGradeOptions).Id);
        Assert.Equal(gradeId, Assert.Single(push.Request.ItemPriceGrades).Id);

        var storedOption = await db.PriceGradeOptions
            .AsNoTracking()
            .SingleAsync(option => option.Id == optionId);
        Assert.False(storedOption.IsDirty);
        Assert.Equal(7, storedOption.Revision);
        Assert.Equal(optionUpdatedAtUtc, storedOption.UpdatedAtUtc);
        Assert.False(await db.SyncOutboxEntries
            .AsNoTracking()
            .AnyAsync(entry =>
                entry.EntityName == nameof(LocalPriceGradeOption) &&
                entry.EntityId == optionId));
        Assert.Equal(
            "Acknowledged",
            await db.SyncOutboxEntries
                .AsNoTracking()
                .Where(entry =>
                    entry.EntityName == nameof(LocalItemPriceGrade) &&
                    entry.EntityId == gradeId)
                .Select(entry => entry.Status)
                .SingleAsync());
    }

    [Fact]
    public async Task FlushPendingChangesAsync_GlobalAdmin_ReferenceOnlyRentalDependencies_DoNotCreateOutboxOrChangeLocalState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await EnsureDiagnosticsDatabaseAsync();

        var now = DateTime.UtcNow;
        var managementCompanyId = Guid.NewGuid();
        var billingProfileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var referencedAssetId = Guid.NewGuid();
        var referencedItemId = Guid.NewGuid();
        var assignmentHistoryId = Guid.NewGuid();
        var billingLogId = Guid.NewGuid();
        var managementCompanyUpdatedAtUtc = now.AddDays(-3);
        var billingProfileUpdatedAtUtc = now.AddDays(-2);
        var referencedAssetUpdatedAtUtc = now.AddDays(-2);
        var referencedItemUpdatedAtUtc = now.AddDays(-3);
        db.Items.Add(new LocalItem
        {
            Id = referencedItemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "REFERENCE ONLY RENTAL ITEM",
            NameMatchKey = "REFERENCE ONLY RENTAL ITEM",
            ItemKind = ItemKinds.Asset,
            TrackingType = ItemTrackingTypes.Asset,
            IsRental = true,
            IsDirty = false,
            CatalogExtensionSyncPending = false,
            Revision = 3,
            CreatedAtUtc = now.AddDays(-5),
            UpdatedAtUtc = referencedItemUpdatedAtUtc
        });
        db.RentalManagementCompanies.Add(new LocalRentalManagementCompany
        {
            Id = managementCompanyId,
            Code = "ALL",
            Name = "REFERENCE-ONLY-MANAGEMENT",
            IsActive = true,
            IsDirty = false,
            Revision = 5,
            CreatedAtUtc = now.AddDays(-5),
            UpdatedAtUtc = managementCompanyUpdatedAtUtc
        });
        db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
        {
            Id = billingProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ManagementCompanyCode = "ALL",
            ProfileKey = $"REFERENCE-ONLY-PROFILE-{billingProfileId:N}",
            CustomerName = "REFERENCE-ONLY-CUSTOMER",
            IsDirty = false,
            Revision = 6,
            CreatedAtUtc = now.AddDays(-4),
            UpdatedAtUtc = billingProfileUpdatedAtUtc
        });
        db.RentalAssets.AddRange(
            new LocalRentalAsset
            {
                Id = assetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = "ALL",
                BillingProfileId = billingProfileId,
                AssetKey = $"DIRTY-ASSET-{assetId:N}",
                ItemName = "DIRTY REFERENCE ASSET",
                IsDirty = true,
                Revision = 1,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
            },
            new LocalRentalAsset
            {
                Id = referencedAssetId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ManagementCompanyCode = "ALL",
                BillingProfileId = billingProfileId,
                ItemId = referencedItemId,
                AssetKey = $"REFERENCE-ONLY-ASSET-{referencedAssetId:N}",
                ItemName = "REFERENCE ONLY RENTAL ITEM",
                IsDirty = false,
                Revision = 4,
                CreatedAtUtc = now.AddDays(-4),
                UpdatedAtUtc = referencedAssetUpdatedAtUtc
            });
        db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
        {
            Id = assignmentHistoryId,
            AssetId = referencedAssetId,
            BillingProfileId = billingProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            IsCurrent = true,
            IsDirty = true,
            Revision = 1,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        db.RentalBillingLogs.Add(new LocalRentalBillingLog
        {
            Id = billingLogId,
            BillingProfileId = billingProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BillingYearMonth = "2026-07",
            IsDirty = true,
            Revision = 1,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();
        await db.Items
            .Where(item => item.Id == referencedItemId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.IsDirty, false)
                .SetProperty(item => item.CatalogExtensionSyncPending, false));

        var session = new SessionState();
        session.SetSession("global-admin-token", new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "global-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });

        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(
            db,
            new OfficeAccessService(),
            dispatcher,
            session);
        await localState.SetSettingAsync("LastSyncRevision", "1");
        var handler = new CaptureBusinessDatabasePushHandler();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);
        using var sync = new SyncService(
            db,
            localState,
            new RentalStateService(db),
            api,
            session,
            dispatcher,
            new SyncDiagnosticsService(session));

        Assert.True(await sync.FlushPendingChangesAsync());

        var push = Assert.Single(handler.PushRequests);
        Assert.Equal(
            managementCompanyId,
            Assert.Single(push.Request.RentalManagementCompanies).Id);
        Assert.Equal(
            billingProfileId,
            Assert.Single(push.Request.RentalBillingProfiles).Id);
        Assert.Equal(
            new[] { assetId, referencedAssetId }.OrderBy(id => id).ToArray(),
            push.Request.RentalAssets.Select(asset => asset.Id).OrderBy(id => id).ToArray());
        Assert.Equal(
            assignmentHistoryId,
            Assert.Single(push.Request.RentalAssetAssignmentHistories).Id);
        Assert.Equal(
            billingLogId,
            Assert.Single(push.Request.RentalBillingLogs).Id);
        Assert.Contains(push.Request.Items, item => item.Id == referencedItemId);

        var storedCompany = await db.RentalManagementCompanies
            .AsNoTracking()
            .SingleAsync(company => company.Id == managementCompanyId);
        Assert.False(storedCompany.IsDirty);
        Assert.Equal(5, storedCompany.Revision);
        Assert.Equal(managementCompanyUpdatedAtUtc, storedCompany.UpdatedAtUtc);
        var storedProfile = await db.RentalBillingProfiles
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == billingProfileId);
        Assert.False(storedProfile.IsDirty);
        Assert.Equal(6, storedProfile.Revision);
        Assert.Equal(billingProfileUpdatedAtUtc, storedProfile.UpdatedAtUtc);
        var storedReferencedAsset = await db.RentalAssets
            .AsNoTracking()
            .SingleAsync(asset => asset.Id == referencedAssetId);
        Assert.False(storedReferencedAsset.IsDirty);
        Assert.Equal(4, storedReferencedAsset.Revision);
        Assert.Equal(referencedAssetUpdatedAtUtc, storedReferencedAsset.UpdatedAtUtc);
        var storedReferencedItem = await db.Items
            .AsNoTracking()
            .SingleAsync(item => item.Id == referencedItemId);
        Assert.False(storedReferencedItem.IsDirty);
        Assert.Equal(3, storedReferencedItem.Revision);
        Assert.Equal(referencedItemUpdatedAtUtc, storedReferencedItem.UpdatedAtUtc);

        Assert.False(await db.SyncOutboxEntries
            .AsNoTracking()
            .AnyAsync(entry =>
                entry.EntityId == managementCompanyId ||
                entry.EntityId == billingProfileId ||
                entry.EntityId == referencedAssetId ||
                entry.EntityId == referencedItemId));
        Assert.Equal(
            3,
            await db.SyncOutboxEntries
                .AsNoTracking()
                .CountAsync(entry =>
                    entry.Status == "Acknowledged" &&
                    (entry.EntityId == assetId ||
                     entry.EntityId == assignmentHistoryId ||
                     entry.EntityId == billingLogId)));
    }

    [Fact]
    public async Task FlushPendingChangesAsync_GlobalAdmin_HistoryAndBillingLogEachCloseTheirOwnCleanParents()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await EnsureDiagnosticsDatabaseAsync();

        var now = DateTime.UtcNow;
        var historyProfileId = Guid.NewGuid();
        var logProfileId = Guid.NewGuid();
        var referencedAssetId = Guid.NewGuid();
        var historyId = Guid.NewGuid();
        var logId = Guid.NewGuid();
        db.RentalBillingProfiles.AddRange(
            new LocalRentalBillingProfile
            {
                Id = historyProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"HISTORY-PARENT-{historyProfileId:N}",
                CustomerName = "HISTORY PARENT",
                IsDirty = false,
                Revision = 4,
                CreatedAtUtc = now.AddDays(-4),
                UpdatedAtUtc = now.AddDays(-2)
            },
            new LocalRentalBillingProfile
            {
                Id = logProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"LOG-PARENT-{logProfileId:N}",
                CustomerName = "LOG PARENT",
                IsDirty = false,
                Revision = 5,
                CreatedAtUtc = now.AddDays(-4),
                UpdatedAtUtc = now.AddDays(-2)
            });
        db.RentalAssets.Add(new LocalRentalAsset
        {
            Id = referencedAssetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            AssetKey = $"HISTORY-ASSET-{referencedAssetId:N}",
            IsDirty = false,
            Revision = 3,
            CreatedAtUtc = now.AddDays(-4),
            UpdatedAtUtc = now.AddDays(-2)
        });
        db.RentalAssetAssignmentHistories.Add(new LocalRentalAssetAssignmentHistory
        {
            Id = historyId,
            AssetId = referencedAssetId,
            BillingProfileId = historyProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            IsCurrent = true,
            IsDirty = true,
            Revision = 1,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        db.RentalBillingLogs.Add(new LocalRentalBillingLog
        {
            Id = logId,
            BillingProfileId = logProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            BillingYearMonth = "2026-07",
            IsDirty = true,
            Revision = 1,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();

        var session = new SessionState();
        session.SetSession("global-admin-token", new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "global-admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        await localState.SetSettingAsync("LastSyncRevision", "1");
        var handler = new CaptureBusinessDatabasePushHandler();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);
        using var sync = new SyncService(
            db,
            localState,
            new RentalStateService(db),
            api,
            session,
            dispatcher,
            new SyncDiagnosticsService(session));

        Assert.True(await sync.FlushPendingChangesAsync());

        var push = Assert.Single(handler.PushRequests);
        Assert.Equal(
            new[] { historyProfileId, logProfileId }.OrderBy(id => id).ToArray(),
            push.Request.RentalBillingProfiles
                .Select(profile => profile.Id)
                .OrderBy(id => id)
                .ToArray());
        Assert.Equal(referencedAssetId, Assert.Single(push.Request.RentalAssets).Id);
        Assert.Equal(historyId, Assert.Single(push.Request.RentalAssetAssignmentHistories).Id);
        Assert.Equal(logId, Assert.Single(push.Request.RentalBillingLogs).Id);
        Assert.False(await db.SyncOutboxEntries
            .AsNoTracking()
            .AnyAsync(entry =>
                entry.EntityId == historyProfileId ||
                entry.EntityId == logProfileId ||
                entry.EntityId == referencedAssetId));
        Assert.Equal(
            2,
            await db.SyncOutboxEntries
                .AsNoTracking()
                .CountAsync(entry =>
                    entry.Status == "Acknowledged" &&
                    (entry.EntityId == historyId || entry.EntityId == logId)));
        Assert.Equal(
            4,
            (await db.RentalBillingProfiles
                .AsNoTracking()
                .SingleAsync(profile => profile.Id == historyProfileId)).Revision);
        Assert.Equal(
            5,
            (await db.RentalBillingProfiles
                .AsNoTracking()
                .SingleAsync(profile => profile.Id == logProfileId)).Revision);
        Assert.Equal(
            3,
            (await db.RentalAssets
                .AsNoTracking()
                .SingleAsync(asset => asset.Id == referencedAssetId)).Revision);
    }

    [Fact]
    public async Task FlushPendingChangesAsync_OfficeRentalEditors_ReferenceOnlyProfileDoesNotCreateOutboxOrChangeLocalState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await EnsureDiagnosticsDatabaseAsync();

        var now = DateTime.UtcNow;
        var profileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var profileUpdatedAtUtc = now.AddDays(-2);
        db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = $"REFERENCE-ONLY-PROFILE-{profileId:N}",
            CustomerName = "연수구 참조 전용 거래처",
            IsDirty = false,
            Revision = 6,
            CreatedAtUtc = now.AddDays(-4),
            UpdatedAtUtc = profileUpdatedAtUtc
        });
        db.RentalAssets.Add(new LocalRentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            BillingProfileId = profileId,
            AssetKey = $"DIRTY-ASSET-{assetId:N}",
            ItemName = "연수구 변경 자산",
            IsDirty = true,
            Revision = 1,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();

        var session = CreateOfficeRentalSession(
            AppPermissionNames.RentalProfileEdit,
            AppPermissionNames.RentalAssetEdit);
        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        await localState.SetSettingAsync("LastSyncRevision", "1");
        var handler = new CaptureBusinessDatabasePushHandler();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);
        using var sync = new SyncService(
            db,
            localState,
            new RentalStateService(db),
            api,
            session,
            dispatcher,
            new SyncDiagnosticsService(session));

        Assert.True(await sync.FlushPendingChangesAsync());

        var push = Assert.Single(handler.PushRequests);
        Assert.Equal(profileId, Assert.Single(push.Request.RentalBillingProfiles).Id);
        Assert.Equal(assetId, Assert.Single(push.Request.RentalAssets).Id);
        var storedProfile = await db.RentalBillingProfiles
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.False(storedProfile.IsDirty);
        Assert.Equal(6, storedProfile.Revision);
        Assert.Equal(profileUpdatedAtUtc, storedProfile.UpdatedAtUtc);
        Assert.False(await db.SyncOutboxEntries
            .AsNoTracking()
            .AnyAsync(entry =>
                entry.EntityName == nameof(LocalRentalBillingProfile) &&
                entry.EntityId == profileId));
        Assert.Equal(
            "Acknowledged",
            await db.SyncOutboxEntries
                .AsNoTracking()
                .Where(entry =>
                    entry.EntityName == nameof(LocalRentalAsset) &&
                    entry.EntityId == assetId)
                .Select(entry => entry.Status)
                .SingleAsync());
    }

    [Fact]
    public async Task FlushPendingChangesAsync_OfficeRentalEditors_DoNotMisclassifyOutOfScopeDirtyProfileAsDependency()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await EnsureDiagnosticsDatabaseAsync();

        var now = DateTime.UtcNow;
        var outOfScopeProfileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
        {
            Id = outOfScopeProfileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            ProfileKey = $"OUT-OF-SCOPE-{outOfScopeProfileId:N}",
            CustomerName = "유즈넷 담당 거래처",
            IsDirty = true,
            Revision = 6,
            CreatedAtUtc = now.AddDays(-4),
            UpdatedAtUtc = now.AddDays(-1)
        });
        db.RentalAssets.Add(new LocalRentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            BillingProfileId = outOfScopeProfileId,
            AssetKey = $"YEONSU-ASSET-{assetId:N}",
            IsDirty = true,
            Revision = 1,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();

        var session = CreateOfficeRentalSession(
            AppPermissionNames.RentalProfileEdit,
            AppPermissionNames.RentalAssetEdit);
        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        await localState.SetSettingAsync("LastSyncRevision", "1");
        var handler = new CaptureBusinessDatabasePushHandler();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);
        using var sync = new SyncService(
            db,
            localState,
            new RentalStateService(db),
            api,
            session,
            dispatcher,
            new SyncDiagnosticsService(session));

        Assert.True(await sync.FlushPendingChangesAsync());

        var push = Assert.Single(handler.PushRequests);
        Assert.Empty(push.Request.RentalBillingProfiles);
        Assert.Equal(assetId, Assert.Single(push.Request.RentalAssets).Id);
        Assert.True((await db.RentalBillingProfiles
            .AsNoTracking()
            .SingleAsync(profile => profile.Id == outOfScopeProfileId)).IsDirty);
        Assert.False(await db.SyncOutboxEntries
            .AsNoTracking()
            .AnyAsync(entry =>
                entry.EntityName == nameof(LocalRentalBillingProfile) &&
                entry.EntityId == outOfScopeProfileId));
        Assert.True(await localState.HasPendingSyncChangesAsync());
        Assert.False(await localState.HasPendingSyncChangesAsync(session));
    }

    [Fact]
    public async Task FlushPendingChangesAsync_AssetEditorWithoutProfilePermission_DoesNotSendReferencedProfile()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await EnsureDiagnosticsDatabaseAsync();

        var now = DateTime.UtcNow;
        var profileId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
        {
            Id = profileId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            ProfileKey = $"EXISTING-PROFILE-{profileId:N}",
            CustomerName = "연수구 기존 거래처",
            IsDirty = false,
            Revision = 6,
            CreatedAtUtc = now.AddDays(-4),
            UpdatedAtUtc = now.AddDays(-2)
        });
        db.RentalAssets.Add(new LocalRentalAsset
        {
            Id = assetId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
            BillingProfileId = profileId,
            AssetKey = $"ASSET-ONLY-{assetId:N}",
            ItemName = "자산 전용 권한 변경",
            IsDirty = true,
            Revision = 1,
            CreatedAtUtc = now.AddDays(-1),
            UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();

        var session = CreateOfficeRentalSession(AppPermissionNames.RentalAssetEdit);
        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        await localState.SetSettingAsync("LastSyncRevision", "1");
        var handler = new CaptureBusinessDatabasePushHandler();
        var api = new ErpApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            session);
        using var sync = new SyncService(
            db,
            localState,
            new RentalStateService(db),
            api,
            session,
            dispatcher,
            new SyncDiagnosticsService(session));

        Assert.True(await sync.FlushPendingChangesAsync());

        var push = Assert.Single(handler.PushRequests);
        Assert.Empty(push.Request.RentalBillingProfiles);
        Assert.Equal(assetId, Assert.Single(push.Request.RentalAssets).Id);
        Assert.False(await db.SyncOutboxEntries
            .AsNoTracking()
            .AnyAsync(entry =>
                entry.EntityName == nameof(LocalRentalBillingProfile) &&
                entry.EntityId == profileId));
        Assert.Equal(
            "Acknowledged",
            await db.SyncOutboxEntries
                .AsNoTracking()
                .Where(entry =>
                    entry.EntityName == nameof(LocalRentalAsset) &&
                    entry.EntityId == assetId)
                .Select(entry => entry.Status)
                .SingleAsync());
    }

    [Fact]
    public async Task FlushPendingChangesAsync_ProfileEditorWithoutSettingsPermission_IgnoresUnsyncableManagementCompanyDirty()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new LocalDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await EnsureDiagnosticsDatabaseAsync();

        var now = DateTime.UtcNow;
        db.RentalManagementCompanies.Add(new LocalRentalManagementCompany
        {
                Id = Guid.NewGuid(),
                Code = OfficeCodeCatalog.Usenet,
                Name = "유즈넷",
                IsSystemDefault = true,
                IsActive = true,
                Revision = 3,
                IsDirty = true,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
        });
        var profileId = Guid.NewGuid();
        db.RentalBillingProfiles.Add(new LocalRentalBillingProfile
        {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Yeonsu,
                ManagementCompanyCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"SYNC-PROFILE-{profileId:N}",
                CustomerName = "연수구 테스트 거래처",
                BillingCycleMonths = 3,
                Revision = 7,
                IsDirty = true,
                CreatedAtUtc = now.AddDays(-1),
                UpdatedAtUtc = now
        });
        await db.SaveChangesAsync();

        var session = new SessionState();
        session.SetSession("test-token", new UserSessionDto
        {
                Username = "yeonsu",
                Role = DomainConstants.RoleUser,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Yeonsu,
                ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
                Permissions =
                [
                    AppPermissionNames.RentalProfileEdit,
                    AppPermissionNames.RentalAssetEdit
                ]
        });

        var dispatcher = new SyncRequestDispatcher();
        var localState = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rentalState = new RentalStateService(db);
        var handler = new CaptureRentalPushHandler();
        var api = new ErpApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, session);
        var diagnostics = new SyncDiagnosticsService(session);
        using var sync = new SyncService(db, localState, rentalState, api, session, dispatcher, diagnostics);

        var flushed = await sync.FlushPendingChangesAsync();

        Assert.True(flushed);
        Assert.NotNull(handler.LastPushRequest);
        Assert.Empty(handler.LastPushRequest!.RentalManagementCompanies);
        Assert.Equal(profileId, Assert.Single(handler.LastPushRequest.RentalBillingProfiles).Id);
        Assert.True((await db.RentalManagementCompanies.AsNoTracking().SingleAsync()).IsDirty);
        Assert.False((await db.RentalBillingProfiles.AsNoTracking().SingleAsync()).IsDirty);
        Assert.True(await localState.HasPendingSyncChangesAsync());
        Assert.False(await localState.HasPendingSyncChangesAsync(session));
    }

    private sealed class CaptureRentalPushHandler : HttpMessageHandler
    {
        public SyncPushRequest? LastPushRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.RequestUri!.AbsolutePath.Equals("/sync/push", StringComparison.OrdinalIgnoreCase))
            {
                if (request.RequestUri.AbsolutePath.Equals("/sync/pull", StringComparison.OrdinalIgnoreCase))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            JsonSerializer.Serialize(new SyncPullResponse { CurrentServerRevision = 8 }),
                            Encoding.UTF8,
                            "application/json")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            }

            LastPushRequest = await request.Content!.ReadFromJsonAsync<SyncPushRequest>(cancellationToken: cancellationToken);
            var profile = Assert.Single(LastPushRequest!.RentalBillingProfiles);
            var result = new SyncPushResult
            {
                AcceptedCount = 1,
                CurrentServerRevision = 8,
                AcceptedRevisions =
                [
                    new SyncAcceptedRevisionDto
                    {
                        EntityName = nameof(LocalRentalBillingProfile),
                        EntityId = profile.Id,
                        Revision = 8,
                        UpdatedAtUtc = DateTime.UtcNow
                    }
                ]
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json")
            };
        }
    }

    private static async Task EnsureDiagnosticsDatabaseAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.LocalDbFile)!);
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={AppPaths.LocalDbFile};Pooling=False")
            .Options;
        await using var diagnosticsDb = new LocalDbContext(options);
        await diagnosticsDb.Database.EnsureCreatedAsync();
    }

    private static SessionState CreateOfficeRentalSession(params string[] permissions)
    {
        var session = new SessionState();
        session.SetSession("office-user-token", new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "yeonsu",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Yeonsu,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private sealed class CaptureBusinessDatabasePushHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _remainingFailures = new(StringComparer.OrdinalIgnoreCase);

        public List<CapturedPush> PushRequests { get; } = [];

        public void FailNextPush(string businessDatabaseName)
            => _remainingFailures[businessDatabaseName] = 1;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Equals("/sync/pull", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(new SyncPullResponse { CurrentServerRevision = 10 });
            }

            if (!request.RequestUri.AbsolutePath.Equals("/sync/push", StringComparison.OrdinalIgnoreCase))
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            var databaseName = request.Headers.TryGetValues("X-Tenant-Code", out var values)
                ? values.Single()
                : string.Empty;
            var pushRequest = await request.Content!.ReadFromJsonAsync<SyncPushRequest>(
                cancellationToken: cancellationToken);
            Assert.NotNull(pushRequest);
            PushRequests.Add(new CapturedPush(databaseName, pushRequest!));

            if (_remainingFailures.TryGetValue(databaseName, out var remainingFailures) &&
                remainingFailures > 0)
            {
                _remainingFailures[databaseName] = remainingFailures - 1;
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            var acceptedAtUtc = DateTime.UtcNow.AddMinutes(1);
            var acceptedRevisions = pushRequest!.Customers
                .Select(customer => new SyncAcceptedRevisionDto
                {
                    EntityName = nameof(LocalCustomer),
                    EntityId = customer.Id,
                    Revision = Math.Max(2, customer.Revision + 1),
                    UpdatedAtUtc = acceptedAtUtc
                })
                .Concat(pushRequest.Items
                .Select(item => new SyncAcceptedRevisionDto
                {
                    EntityName = nameof(LocalItem),
                    EntityId = item.Id,
                    Revision = Math.Max(2, item.Revision + 1),
                    UpdatedAtUtc = acceptedAtUtc
                }))
                .Concat(pushRequest.RentalAssets.Select(asset => new SyncAcceptedRevisionDto
                {
                    EntityName = nameof(LocalRentalAsset),
                    EntityId = asset.Id,
                    Revision = Math.Max(2, asset.Revision + 1),
                    UpdatedAtUtc = acceptedAtUtc
                }))
                .Concat(pushRequest.RentalBillingProfiles.Select(profile => new SyncAcceptedRevisionDto
                {
                    EntityName = nameof(LocalRentalBillingProfile),
                    EntityId = profile.Id,
                    Revision = Math.Max(2, profile.Revision + 1),
                    UpdatedAtUtc = acceptedAtUtc
                }))
                .Concat(pushRequest.RentalAssetAssignmentHistories.Select(history => new SyncAcceptedRevisionDto
                {
                    EntityName = nameof(LocalRentalAssetAssignmentHistory),
                    EntityId = history.Id,
                    Revision = Math.Max(2, history.Revision + 1),
                    UpdatedAtUtc = acceptedAtUtc
                }))
                .Concat(pushRequest.RentalBillingLogs.Select(log => new SyncAcceptedRevisionDto
                {
                    EntityName = nameof(LocalRentalBillingLog),
                    EntityId = log.Id,
                    Revision = Math.Max(2, log.Revision + 1),
                    UpdatedAtUtc = acceptedAtUtc
                }))
                .Concat(pushRequest.ItemPriceGrades.Select(priceGrade => new SyncAcceptedRevisionDto
                {
                    EntityName = nameof(LocalItemPriceGrade),
                    EntityId = priceGrade.Id,
                    Revision = Math.Max(2, priceGrade.Revision + 1),
                    UpdatedAtUtc = acceptedAtUtc
                }))
                .Concat(pushRequest.PriceGradeOptions.Select(option => new SyncAcceptedRevisionDto
                {
                    EntityName = nameof(LocalPriceGradeOption),
                    EntityId = option.Id,
                    Revision = Math.Max(2, option.Revision + 1),
                    UpdatedAtUtc = acceptedAtUtc
                }))
                .ToList();

            return JsonResponse(new SyncPushResult
            {
                AcceptedCount = acceptedRevisions.Count,
                CurrentServerRevision = 10,
                AcceptedRevisions = acceptedRevisions,
                AcceptedItemWarehouseStockKeys =
                    pushRequest.ItemWarehouseStocks
                        .Select(stock =>
                            new SyncAcceptedItemWarehouseStockKeyDto
                            {
                                ItemId = stock.ItemId,
                                WarehouseCode =
                                    stock.WarehouseCode
                            })
                        .ToList()
            });
        }

        private static HttpResponseMessage JsonResponse<T>(T payload)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json")
            };
    }

    private sealed class CanonicalPrimaryDependencyConflictHandler
        : HttpMessageHandler
    {
        private readonly Guid _canonicalServerCompanyId;

        public CanonicalPrimaryDependencyConflictHandler(
            Guid canonicalServerCompanyId)
        {
            _canonicalServerCompanyId = canonicalServerCompanyId;
        }

        public List<CapturedPush> PushRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Equals(
                    "/sync/pull",
                    StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse(
                    new SyncPullResponse
                    {
                        CurrentServerRevision = 10
                    });
            }

            if (!request.RequestUri.AbsolutePath.Equals(
                    "/sync/push",
                    StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(
                    HttpStatusCode.NotFound);
            }

            var databaseName = request.Headers.TryGetValues(
                "X-Tenant-Code",
                out var values)
                ? values.Single()
                : string.Empty;
            var pushRequest =
                await request.Content!
                    .ReadFromJsonAsync<SyncPushRequest>(
                        cancellationToken: cancellationToken);
            Assert.NotNull(pushRequest);
            PushRequests.Add(
                new CapturedPush(databaseName, pushRequest!));

            var acceptedAtUtc = DateTime.UtcNow.AddMinutes(1);
            var acceptedRevisions = pushRequest!.Items
                .Select(item => new SyncAcceptedRevisionDto
                {
                    EntityName = nameof(LocalItem),
                    EntityId = item.Id,
                    Revision = Math.Max(2, item.Revision + 1),
                    UpdatedAtUtc = acceptedAtUtc
                })
                .Concat(
                    pushRequest.RentalBillingProfiles.Select(
                        profile => new SyncAcceptedRevisionDto
                        {
                            EntityName =
                                nameof(LocalRentalBillingProfile),
                            EntityId = profile.Id,
                            Revision =
                                Math.Max(2, profile.Revision + 1),
                            UpdatedAtUtc = acceptedAtUtc
                        }))
                .ToList();
            var result = new SyncPushResult
            {
                AcceptedCount = acceptedRevisions.Count,
                CurrentServerRevision = 10,
                AcceptedRevisions = acceptedRevisions
            };

            if (string.Equals(
                    databaseName,
                    "USENET",
                    StringComparison.OrdinalIgnoreCase))
            {
                var company = Assert.Single(
                    pushRequest.RentalManagementCompanies);
                var canonicalClient =
                    JsonSerializer.Deserialize<
                        RentalManagementCompanyDto>(
                        JsonSerializer.Serialize(company))!;
                canonicalClient.Id = _canonicalServerCompanyId;
                var canonicalServer =
                    JsonSerializer.Deserialize<
                        RentalManagementCompanyDto>(
                        JsonSerializer.Serialize(canonicalClient))!;
                canonicalServer.Revision =
                    Math.Max(8, company.Revision + 1);
                canonicalServer.ExpectedRevision =
                    canonicalServer.Revision;
                canonicalServer.UpdatedAtUtc = acceptedAtUtc;
                result.ConflictCount = 1;
                result.Conflicts =
                [
                    new ConflictLogDto
                    {
                        EntityName =
                            nameof(RentalManagementCompanyDto)
                                .Replace("Dto", string.Empty),
                        EntityId =
                            _canonicalServerCompanyId.ToString("D"),
                        Reason =
                            "Expected revision mismatch. client=7, server=8",
                        ClientJson =
                            JsonSerializer.Serialize(canonicalClient),
                        ServerJson =
                            JsonSerializer.Serialize(canonicalServer)
                    }
                ];
            }

            return JsonResponse(result);
        }

        private static HttpResponseMessage JsonResponse<T>(T payload)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json")
            };
    }

    private sealed record CapturedPush(
        string BusinessDatabaseName,
        SyncPushRequest Request);
}
