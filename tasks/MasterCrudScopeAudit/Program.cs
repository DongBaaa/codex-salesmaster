using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Infrastructure;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> Main()
    {
        PrepareIsolatedLocalAppData();

        var checks = new List<AuditCheck>();
        var startedAtUtc = DateTime.UtcNow;

        await using var db = new LocalDbContext();
        await LocalDbInitializer.InitializeAsync(db);

        var officeAccess = new OfficeAccessService();
        var syncDispatcher = new SyncRequestDispatcher();
        var usenetSession = CreateSession(
            username: "audit-usenet-editor",
            role: DomainConstants.RoleUser,
            officeCode: OfficeCodeCatalog.Usenet,
            tenantCode: TenantScopeCatalog.UsenetGroup,
            scopeType: TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.CustomerEdit,
            AppPermissionNames.ItemEdit);
        var yeonsuSession = CreateSession(
            username: "audit-yeonsu-editor",
            role: DomainConstants.RoleUser,
            officeCode: OfficeCodeCatalog.Yeonsu,
            tenantCode: TenantScopeCatalog.UsenetGroup,
            scopeType: TenantScopeCatalog.ScopeOfficeOnly,
            AppPermissionNames.CustomerEdit,
            AppPermissionNames.ItemEdit);
        var noEditSession = CreateSession(
            username: "audit-usenet-viewer",
            role: DomainConstants.RoleUser,
            officeCode: OfficeCodeCatalog.Usenet,
            tenantCode: TenantScopeCatalog.UsenetGroup,
            scopeType: TenantScopeCatalog.ScopeOfficeOnly);
        var adminSession = CreateSession(
            username: "audit-admin",
            role: DomainConstants.RoleAdmin,
            officeCode: OfficeCodeCatalog.Usenet,
            tenantCode: TenantScopeCatalog.UsenetGroup,
            scopeType: TenantScopeCatalog.ScopeAdmin,
            AppPermissionNames.CustomerEdit,
            AppPermissionNames.ItemEdit);

        var local = new LocalStateService(db, officeAccess, syncDispatcher, usenetSession);

        await RunCustomerCrudScopeAuditAsync(db, local, checks, usenetSession, yeonsuSession, noEditSession, adminSession);
        await RunItemCrudScopeAuditAsync(db, local, checks, usenetSession, yeonsuSession, noEditSession, adminSession);

        var failedChecks = checks.Where(check => !check.Passed).ToList();
        var report = new
        {
            Status = failedChecks.Count == 0 ? "pass" : "fail",
            StartedAtUtc = startedAtUtc,
            FinishedAtUtc = DateTime.UtcNow,
            LocalDbFile = AppPaths.LocalDbFile,
            CheckCount = checks.Count,
            FailedCheckCount = failedChecks.Count,
            Checks = checks
        };

        var evidenceDirectory = Path.Combine(AppContext.BaseDirectory, "runtime", "master-crud-scope-audit-evidence");
        Directory.CreateDirectory(evidenceDirectory);
        var reportPath = Path.Combine(evidenceDirectory, "master-crud-scope-audit.json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions));

        Console.WriteLine(reportPath);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            report.Status,
            report.CheckCount,
            report.FailedCheckCount,
            report.LocalDbFile
        }, JsonOptions));

        SqliteConnection.ClearAllPools();
        return failedChecks.Count == 0 ? 0 : 1;
    }

    private static async Task RunCustomerCrudScopeAuditAsync(
        LocalDbContext db,
        LocalStateService local,
        List<AuditCheck> checks,
        SessionState usenetSession,
        SessionState yeonsuSession,
        SessionState noEditSession,
        SessionState adminSession)
    {
        var customerId = Guid.Parse("df0125d7-cb1e-4f59-8c32-7a8c0d7c9a01");
        var customer = new LocalCustomer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Audit Customer Alpha",
            NameMatchKey = "AUDITCUSTOMERALPHA",
            TradeType = CustomerTradeTypes.Sales,
            BusinessNumber = "AUDIT-CUST-001",
            Phone = "010-0000-0001",
            Notes = "master crud scope audit"
        };

        var createResult = await local.UpsertCustomerAsync(customer, usenetSession);
        AddCheck(checks, "customer.create.edit-permission.success", createResult.Success, createResult.Message);

        db.ChangeTracker.Clear();
        var stored = await db.Customers.IgnoreQueryFilters().SingleAsync(row => row.Id == customerId);
        AddCheck(checks, "customer.create.db-persisted", !stored.IsDeleted && stored.IsDirty && stored.ResponsibleOfficeCode == OfficeCodeCatalog.Usenet && stored.TenantCode == TenantScopeCatalog.UsenetGroup, new { stored.IsDeleted, stored.IsDirty, stored.ResponsibleOfficeCode, stored.TenantCode });

        var noEditCreate = await local.UpsertCustomerAsync(new LocalCustomer
        {
            Id = Guid.Parse("df0125d7-cb1e-4f59-8c32-7a8c0d7c9a02"),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "No Permission Customer",
            NameMatchKey = "NOPERMISSIONCUSTOMER",
            TradeType = CustomerTradeTypes.Sales
        }, noEditSession);
        AddCheck(checks, "customer.create.no-edit-permission.denied", noEditCreate.PermissionDenied && !noEditCreate.Success, noEditCreate.Message);

        var usenetCustomers = await local.GetCustomersAsync(usenetSession);
        var yeonsuCustomers = await local.GetCustomersAsync(yeonsuSession);
        var adminCustomers = await local.GetCustomersAsync(adminSession);
        AddCheck(checks, "customer.scope.usenet-visible", usenetCustomers.Any(row => row.Id == customerId), usenetCustomers.Select(row => row.Id).ToArray());
        AddCheck(checks, "customer.scope.other-office-hidden", yeonsuCustomers.All(row => row.Id != customerId), yeonsuCustomers.Select(row => row.Id).ToArray());
        AddCheck(checks, "customer.scope.admin-visible", adminCustomers.Any(row => row.Id == customerId), adminCustomers.Select(row => row.Id).ToArray());

        var deniedCrossOffice = await local.UpsertCustomerAsync(new LocalCustomer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Cross Office Mutation Should Not Persist",
            NameMatchKey = "CROSSOFFICEMUTATIONSHOULDNOTPERSIST",
            TradeType = CustomerTradeTypes.Sales
        }, yeonsuSession);
        db.ChangeTracker.Clear();
        var afterDeniedCrossOffice = await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == customerId);
        AddCheck(checks, "customer.update.other-office.denied", deniedCrossOffice.PermissionDenied && afterDeniedCrossOffice.NameOriginal == "Audit Customer Alpha", new { deniedCrossOffice.Message, afterDeniedCrossOffice.NameOriginal });

        stored = await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == customerId);
        var updateCandidate = CloneCustomer(stored);
        updateCandidate.NameOriginal = "Audit Customer Alpha Updated";
        updateCandidate.NameMatchKey = "AUDITCUSTOMERALPHAUPDATED";
        updateCandidate.Phone = "010-0000-0002";
        var updateResult = await local.UpsertCustomerAsync(updateCandidate, usenetSession);
        db.ChangeTracker.Clear();
        var afterUpdate = await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == customerId);
        AddCheck(checks, "customer.update.db-persisted", updateResult.Success && afterUpdate.NameOriginal == "Audit Customer Alpha Updated" && afterUpdate.Phone == "010-0000-0002", new { updateResult.Message, afterUpdate.NameOriginal, afterUpdate.Phone });

        var dirtyForNoEdit = await local.GetDirtyCustomersForSyncAsync(noEditSession);
        var dirtyForEditor = await local.GetDirtyCustomersForSyncAsync(usenetSession);
        AddCheck(checks, "customer.sync-dirty.no-edit-filtered", dirtyForNoEdit.All(row => row.Id != customerId), dirtyForNoEdit.Select(row => row.Id).ToArray());
        AddCheck(checks, "customer.sync-dirty.editor-visible", dirtyForEditor.Any(row => row.Id == customerId), dirtyForEditor.Select(row => row.Id).ToArray());

        var deleteNoEdit = await local.DeleteCustomerAsync(customerId, noEditSession);
        AddCheck(checks, "customer.delete.no-edit-permission.denied", deleteNoEdit.PermissionDenied && !deleteNoEdit.Success, deleteNoEdit.Message);

        var deleteResult = await local.DeleteCustomerAsync(customerId, usenetSession, expectedRevision: afterUpdate.Revision);
        db.ChangeTracker.Clear();
        var afterDelete = await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == customerId);
        AddCheck(checks, "customer.delete.soft-delete-db", deleteResult.Success && afterDelete.IsDeleted && afterDelete.IsDirty, new { deleteResult.Message, afterDelete.IsDeleted, afterDelete.IsDirty });
        AddCheck(checks, "customer.delete.hidden-from-active-query", (await local.GetCustomersAsync(usenetSession)).All(row => row.Id != customerId), null);
        AddCheck(checks, "customer.recycle-bin.entry-visible", (await local.GetRecycleBinEntriesAsync(usenetSession)).Any(row => row.EntityId == customerId && row.Kind == RecycleBinEntityKind.Customer), null);

        var restoreNoEdit = await local.RestoreCustomerAsync(customerId, noEditSession);
        AddCheck(checks, "customer.restore.no-edit-permission.denied", restoreNoEdit.PermissionDenied && !restoreNoEdit.Success, restoreNoEdit.Message);

        var restoreResult = await local.RestoreCustomerAsync(customerId, usenetSession);
        db.ChangeTracker.Clear();
        var afterRestore = await db.Customers.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == customerId);
        AddCheck(checks, "customer.restore.db-active", restoreResult.Success && !afterRestore.IsDeleted && afterRestore.IsDirty && (await local.GetCustomersAsync(usenetSession)).Any(row => row.Id == customerId), new { restoreResult.Message, afterRestore.IsDeleted, afterRestore.IsDirty });
    }

    private static async Task RunItemCrudScopeAuditAsync(
        LocalDbContext db,
        LocalStateService local,
        List<AuditCheck> checks,
        SessionState usenetSession,
        SessionState yeonsuSession,
        SessionState noEditSession,
        SessionState adminSession)
    {
        var itemId = Guid.Parse("df0125d7-cb1e-4f59-8c32-7a8c0d7c9b01");
        var item = new LocalItem
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Audit Stock Item",
            NameMatchKey = "AUDITSTOCKITEM",
            SpecificationOriginal = "BOX",
            SpecificationMatchKey = "BOX",
            CategoryName = string.Empty,
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            PurchasePrice = 100m,
            SalePrice = 150m,
            CurrentStock = 0m,
            IsSale = true
        };

        await AssertThrowsUnauthorizedAsync(
            () => local.UpsertItemAsync(CloneItem(item, Guid.Parse("df0125d7-cb1e-4f59-8c32-7a8c0d7c9b02")), noEditSession),
            checks,
            "item.create.no-edit-permission.denied");

        var createdItem = await local.UpsertItemAsync(item, usenetSession, OfficeCodeCatalog.Usenet);
        db.ChangeTracker.Clear();
        var storedItem = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == itemId);
        AddCheck(checks, "item.create.db-persisted", createdItem.Id == itemId && !storedItem.IsDeleted && storedItem.IsDirty && storedItem.OfficeCode == OfficeCodeCatalog.Usenet && storedItem.TenantCode == TenantScopeCatalog.UsenetGroup, new { storedItem.IsDeleted, storedItem.IsDirty, storedItem.OfficeCode, storedItem.TenantCode });

        await local.SetItemOfficeStockAsync(itemId, 5m, OfficeCodeCatalog.Usenet);
        db.ChangeTracker.Clear();
        AddCheck(checks, "item.stock.manual-set.db-consistent", await GetItemCurrentStockAsync(db, itemId) == 5m && await GetWarehouseStockAsync(db, itemId, OfficeCodeCatalog.UsenetMainWarehouse) == 5m, new { ItemCurrentStock = await GetItemCurrentStockAsync(db, itemId), WarehouseStock = await GetWarehouseStockAsync(db, itemId, OfficeCodeCatalog.UsenetMainWarehouse) });

        var usenetItems = await local.GetItemsAsync(usenetSession);
        var yeonsuItems = await local.GetItemsAsync(yeonsuSession);
        var adminItems = await local.GetItemsAsync(adminSession);
        AddCheck(checks, "item.scope.usenet-visible", usenetItems.Any(row => row.Id == itemId), usenetItems.Select(row => row.Id).ToArray());
        AddCheck(checks, "item.scope.other-office-hidden", yeonsuItems.All(row => row.Id != itemId), yeonsuItems.Select(row => row.Id).ToArray());
        AddCheck(checks, "item.scope.admin-visible", adminItems.Any(row => row.Id == itemId), adminItems.Select(row => row.Id).ToArray());

        await AssertThrowsUnauthorizedAsync(
            () => local.UpsertItemAsync(CloneItem(storedItem, itemId, "No Permission Item Mutation"), noEditSession),
            checks,
            "item.update.no-edit-permission.denied");

        await AssertThrowsUnauthorizedAsync(
            () => local.UpsertItemAsync(CloneItem(storedItem, itemId, "Cross Office Item Mutation"), yeonsuSession),
            checks,
            "item.update.other-office.denied");

        var updateCandidate = CloneItem(storedItem, itemId, "Audit Stock Item Updated");
        updateCandidate.SalePrice = 175m;
        var updateResult = await local.UpsertItemAsync(updateCandidate, usenetSession, OfficeCodeCatalog.Usenet);
        db.ChangeTracker.Clear();
        var afterUpdate = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == itemId);
        AddCheck(checks, "item.update.db-persisted", updateResult.NameOriginal == "Audit Stock Item Updated" && afterUpdate.SalePrice == 175m, new { afterUpdate.NameOriginal, afterUpdate.SalePrice });

        var dirtyItemsNoEdit = await local.GetDirtyItemsForSyncAsync(noEditSession);
        var dirtyItemsEditor = await local.GetDirtyItemsForSyncAsync(usenetSession);
        AddCheck(checks, "item.sync-dirty.no-edit-filtered", dirtyItemsNoEdit.All(row => row.Id != itemId), dirtyItemsNoEdit.Select(row => row.Id).ToArray());
        AddCheck(checks, "item.sync-dirty.editor-visible", dirtyItemsEditor.Any(row => row.Id == itemId), dirtyItemsEditor.Select(row => row.Id).ToArray());

        var deleteNoEdit = await local.DeleteItemAsync(itemId, noEditSession, expectedRevision: afterUpdate.Revision);
        AddCheck(checks, "item.delete.no-edit-permission.denied", deleteNoEdit.PermissionDenied && !deleteNoEdit.Success, deleteNoEdit.Message);
        var deleteOtherOffice = await local.DeleteItemAsync(itemId, yeonsuSession, expectedRevision: afterUpdate.Revision);
        AddCheck(checks, "item.delete.other-office.denied", deleteOtherOffice.PermissionDenied && !deleteOtherOffice.Success, deleteOtherOffice.Message);

        var deleteResult = await local.DeleteItemAsync(itemId, usenetSession, expectedRevision: afterUpdate.Revision);
        db.ChangeTracker.Clear();
        var afterDelete = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == itemId);
        AddCheck(checks, "item.delete.soft-delete-db", deleteResult.Success && afterDelete.IsDeleted && afterDelete.IsDirty, new { deleteResult.Message, afterDelete.IsDeleted, afterDelete.IsDirty });
        AddCheck(checks, "item.delete.inventory-snapshot-zeroed", afterDelete.CurrentStock == 0m && await GetWarehouseStockAsync(db, itemId, OfficeCodeCatalog.UsenetMainWarehouse) == 0m, new { afterDelete.CurrentStock, WarehouseStock = await GetWarehouseStockAsync(db, itemId, OfficeCodeCatalog.UsenetMainWarehouse) });
        AddCheck(checks, "item.delete.hidden-from-active-query", (await local.GetItemsAsync(usenetSession)).All(row => row.Id != itemId), null);
        AddCheck(checks, "item.recycle-bin.entry-visible", (await local.GetRecycleBinEntriesAsync(usenetSession)).Any(row => row.EntityId == itemId && row.Kind == RecycleBinEntityKind.Item), null);

        var restoreNoEdit = await local.RestoreItemAsync(itemId, noEditSession);
        AddCheck(checks, "item.restore.no-edit-permission.denied", restoreNoEdit.PermissionDenied && !restoreNoEdit.Success, restoreNoEdit.Message);

        var restoreResult = await local.RestoreItemAsync(itemId, usenetSession);
        db.ChangeTracker.Clear();
        var afterRestore = await db.Items.IgnoreQueryFilters().AsNoTracking().SingleAsync(row => row.Id == itemId);
        var restoredWarehouseStock = await GetWarehouseStockAsync(db, itemId, OfficeCodeCatalog.UsenetMainWarehouse);
        AddCheck(checks, "item.restore.db-active", restoreResult.Success && !afterRestore.IsDeleted && afterRestore.IsDirty && (await local.GetItemsAsync(usenetSession)).Any(row => row.Id == itemId), new { restoreResult.Message, afterRestore.IsDeleted, afterRestore.IsDirty });
        AddCheck(checks, "item.restore.inventory-rebuilt", afterRestore.CurrentStock == 5m && restoredWarehouseStock == 5m, new { afterRestore.CurrentStock, WarehouseStock = restoredWarehouseStock });
    }

    private static LocalCustomer CloneCustomer(LocalCustomer source) => new()
    {
        Id = source.Id,
        CustomerMasterId = source.CustomerMasterId,
        TenantCode = source.TenantCode,
        OfficeCode = source.OfficeCode,
        NameOriginal = source.NameOriginal,
        NameMatchKey = source.NameMatchKey,
        CategoryId = source.CategoryId,
        TradeType = source.TradeType,
        Department = source.Department,
        ContactPerson = source.ContactPerson,
        BusinessNumber = source.BusinessNumber,
        Address = source.Address,
        DetailAddress = source.DetailAddress,
        Phone = source.Phone,
        MobilePhone = source.MobilePhone,
        FaxNumber = source.FaxNumber,
        Email = source.Email,
        HomePage = source.HomePage,
        Representative = source.Representative,
        BusinessType = source.BusinessType,
        BusinessItem = source.BusinessItem,
        Recipient = source.Recipient,
        PriceGrade = source.PriceGrade,
        Notes = source.Notes,
        ResponsibleOfficeCode = source.ResponsibleOfficeCode,
        CreatedAtUtc = source.CreatedAtUtc,
        UpdatedAtUtc = source.UpdatedAtUtc,
        Revision = source.Revision,
        IsDirty = source.IsDirty,
        IsDeleted = source.IsDeleted
    };

    private static LocalItem CloneItem(LocalItem source, Guid? id = null, string? name = null) => new()
    {
        Id = id ?? source.Id,
        TenantCode = source.TenantCode,
        OfficeCode = source.OfficeCode,
        NameOriginal = name ?? source.NameOriginal,
        NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(name ?? source.NameOriginal),
        SpecificationOriginal = source.SpecificationOriginal,
        SpecificationMatchKey = source.SpecificationMatchKey,
        CategoryName = source.CategoryName,
        ItemKind = source.ItemKind,
        TrackingType = source.TrackingType,
        Unit = source.Unit,
        BoxQuantity = source.BoxQuantity,
        StorageLocation = source.StorageLocation,
        CurrentStock = source.CurrentStock,
        SafetyStock = source.SafetyStock,
        PurchasePrice = source.PurchasePrice,
        SalePrice = source.SalePrice,
        RetailPrice = source.RetailPrice,
        PriceGradeA = source.PriceGradeA,
        PriceGradeB = source.PriceGradeB,
        PriceGradeC = source.PriceGradeC,
        LastPurchaseDate = source.LastPurchaseDate,
        LastSaleDate = source.LastSaleDate,
        SimpleMemo = source.SimpleMemo,
        IsRental = source.IsRental,
        IsSale = source.IsSale,
        SerialNumber = source.SerialNumber,
        MaterialNumber = source.MaterialNumber,
        InstallLocation = source.InstallLocation,
        RentalStartDate = source.RentalStartDate,
        RentalEndDate = source.RentalEndDate,
        Notes = source.Notes,
        CreatedAtUtc = source.CreatedAtUtc,
        UpdatedAtUtc = source.UpdatedAtUtc,
        Revision = source.Revision,
        IsDirty = source.IsDirty,
        IsDeleted = source.IsDeleted
    };

    private static async Task AssertThrowsUnauthorizedAsync(Func<Task> action, List<AuditCheck> checks, string name)
    {
        try
        {
            await action();
            AddCheck(checks, name, passed: false, detail: "No exception was thrown.");
        }
        catch (UnauthorizedAccessException ex)
        {
            AddCheck(checks, name, passed: true, detail: ex.Message);
        }
    }

    private static async Task<decimal> GetItemCurrentStockAsync(LocalDbContext db, Guid itemId)
        => await db.Items.IgnoreQueryFilters().Where(row => row.Id == itemId).Select(row => row.CurrentStock).SingleAsync();

    private static async Task<decimal> GetWarehouseStockAsync(LocalDbContext db, Guid itemId, string warehouseCode)
        => await db.ItemWarehouseStocks.Where(row => row.ItemId == itemId && row.WarehouseCode == warehouseCode).Select(row => row.Quantity).SingleOrDefaultAsync();

    private static SessionState CreateSession(
        string username,
        string role,
        string officeCode,
        string tenantCode,
        string scopeType,
        params string[] permissions)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = username,
            Role = role,
            OfficeCode = officeCode,
            TenantCode = tenantCode,
            ScopeType = scopeType,
            Permissions = permissions.Distinct(StringComparer.Ordinal).ToList()
        });
        return session;
    }

    private static void PrepareIsolatedLocalAppData()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "runtime", "master-crud-scope-audit-appdata");
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", root);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", "1");
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", "1");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", Path.Combine(root, "LocalAppData"));
    }

    private static void AddCheck(List<AuditCheck> checks, string name, bool passed, object? detail)
        => checks.Add(new AuditCheck(name, passed, detail == null ? string.Empty : JsonSerializer.Serialize(detail, JsonOptions)));

    private sealed record AuditCheck(string Name, bool Passed, string Detail);
}