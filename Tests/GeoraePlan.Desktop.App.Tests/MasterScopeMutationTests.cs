using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MasterScopeMutationTests
{
    [Fact]
    public async Task CustomerMutation_OfficeOnlyUserCannotSaveOrDeleteOutOfScopeCustomer()
    {
        PrepareAppRoot("georaeplan-customer-scope-mutation");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var hiddenCustomerId = Guid.NewGuid();
            db.Customers.Add(CreateCustomer(hiddenCustomerId, "Hidden Yeonsu Customer", OfficeCodeCatalog.Yeonsu));
            await db.SaveChangesAsync();

            var session = CreateOfficeOnlySession(OfficeCodeCatalog.Usenet, AppPermissionNames.CustomerEdit);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            var deniedSave = await local.UpsertCustomerAsync(
                CreateCustomer(hiddenCustomerId, "Changed Hidden Customer", OfficeCodeCatalog.Yeonsu),
                session);

            Assert.False(deniedSave.Success);
            var afterSave = await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(customer => customer.Id == hiddenCustomerId);
            Assert.Equal("Hidden Yeonsu Customer", afterSave.NameOriginal);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, afterSave.ResponsibleOfficeCode);
            Assert.False(afterSave.IsDeleted);
            Assert.False(afterSave.IsDirty);

            var deniedDelete = await local.DeleteCustomerAsync(hiddenCustomerId, session);

            Assert.False(deniedDelete.Success);
            var afterDelete = await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(customer => customer.Id == hiddenCustomerId);
            Assert.False(afterDelete.IsDeleted);
            Assert.False(afterDelete.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ItemMutation_OfficeOnlyUserCannotSaveOrDeleteOutOfScopeItem()
    {
        PrepareAppRoot("georaeplan-item-scope-mutation");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var hiddenItemId = Guid.NewGuid();
            db.Items.Add(CreateItem(hiddenItemId, "Hidden Yeonsu Item", OfficeCodeCatalog.Yeonsu));
            await db.SaveChangesAsync();

            var session = CreateOfficeOnlySession(OfficeCodeCatalog.Usenet, AppPermissionNames.ItemEdit);
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => local.UpsertItemAsync(
                CreateItem(hiddenItemId, "Changed Hidden Item", OfficeCodeCatalog.Yeonsu),
                session));
            var afterSave = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == hiddenItemId);
            Assert.Equal("Hidden Yeonsu Item", afterSave.NameOriginal);
            Assert.Equal(OfficeCodeCatalog.Yeonsu, afterSave.OfficeCode);
            Assert.False(afterSave.IsDeleted);
            Assert.False(afterSave.IsDirty);

            var deniedDelete = await local.DeleteItemAsync(hiddenItemId, session);

            Assert.False(deniedDelete.Success);
            var afterDelete = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(item => item.Id == hiddenItemId);
            Assert.False(afterDelete.IsDeleted);
            Assert.False(afterDelete.IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task CustomerMerge_OfficeOnlyUserCannotMergeMixedOfficeCandidates()
    {
        PrepareAppRoot("georaeplan-customer-merge-scope-mutation");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var visibleCustomerId = Guid.NewGuid();
            var hiddenCustomerId = Guid.NewGuid();
            db.Customers.AddRange(
                CreateCustomer(visibleCustomerId, "Same Customer", OfficeCodeCatalog.Usenet),
                CreateCustomer(hiddenCustomerId, "Same Customer", OfficeCodeCatalog.Yeonsu));
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var session = CreateOfficeOnlySession(OfficeCodeCatalog.Usenet, AppPermissionNames.CustomerEdit);
            var issue = new DataIntegrityIssueDetail
            {
                Code = DataIntegrityIssueCodes.CustomerDuplicateCandidate,
                RelatedEntityIds = new[] { visibleCustomerId, hiddenCustomerId },
                SuggestedAction = "scope guard"
            };

            var result = await service.MergeDuplicateIssueAsync(issue, session);

            Assert.False(result.Success);
            var customers = await db.Customers.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(customer => customer.Id);
            Assert.False(customers[visibleCustomerId].IsDeleted);
            Assert.False(customers[visibleCustomerId].IsDirty);
            Assert.False(customers[hiddenCustomerId].IsDeleted);
            Assert.False(customers[hiddenCustomerId].IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ItemMerge_OfficeOnlyUserCannotMergeMixedOfficeCandidates()
    {
        PrepareAppRoot("georaeplan-item-merge-scope-mutation");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var visibleItemId = Guid.NewGuid();
            var hiddenItemId = Guid.NewGuid();
            db.Items.AddRange(
                CreateItem(visibleItemId, "Same Item", OfficeCodeCatalog.Usenet),
                CreateItem(hiddenItemId, "Same Item", OfficeCodeCatalog.Yeonsu));
            await db.SaveChangesAsync();

            var service = new DataIntegrityIssueService(db, new SyncRequestDispatcher());
            var session = CreateOfficeOnlySession(OfficeCodeCatalog.Usenet, AppPermissionNames.ItemEdit);
            var issue = new DataIntegrityIssueDetail
            {
                Code = DataIntegrityIssueCodes.ItemDuplicateCandidate,
                RelatedEntityIds = new[] { visibleItemId, hiddenItemId },
                SuggestedAction = "scope guard"
            };

            var result = await service.MergeDuplicateIssueAsync(issue, session);

            Assert.False(result.Success);
            var items = await db.Items.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(item => item.Id);
            Assert.False(items[visibleItemId].IsDeleted);
            Assert.False(items[visibleItemId].IsDirty);
            Assert.False(items[hiddenItemId].IsDeleted);
            Assert.False(items[hiddenItemId].IsDirty);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalCustomer CreateCustomer(Guid id, string name, string officeCode)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            ResponsibleOfficeCode = officeCode,
            NameOriginal = name,
            NameMatchKey = name,
            TradeType = CustomerTradeTypes.Sales,
            IsDeleted = false,
            IsDirty = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalItem CreateItem(Guid id, string name, string officeCode)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            NameOriginal = name,
            NameMatchKey = name,
            SpecificationOriginal = "Scope Spec",
            SpecificationMatchKey = "Scope Spec",
            TrackingType = ItemTrackingTypes.Stock,
            CurrentStock = 0m,
            IsDeleted = false,
            IsDirty = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static SessionState CreateOfficeOnlySession(string officeCode, params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = $"scope-{officeCode}",
            Role = DomainConstants.RoleUser,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = permissions.ToList()
        });
        return session;
    }

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
