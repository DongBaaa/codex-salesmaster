using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// Facade over the local SQLite database for all CRUD operations.
/// All writes mark IsDirty = true; sync service flushes dirty records.
/// </summary>
public sealed partial class LocalStateService
{
    private const string YeonsuOfficeIdSettingKey = "SystemOffice.YeonsuOfficeId";
    private const string CompanyProfileAssignmentPrefix = "CompanyProfile.Assigned.";
    private const long MaxCustomerContractFileSizeBytes = 15 * 1024 * 1024;
    private readonly LocalDbContext _db;
    private readonly OfficeAccessService _officeAccess;
    private readonly SyncRequestDispatcher _syncRequestDispatcher;
    private readonly SessionState _session;
    private bool _hasPendingSyncEntityChanges;
    private int _suppressSyncDispatchCount;
    private string _pendingSyncEntityTypeSummary = string.Empty;
    public event EventHandler? InventoryStateChanged;

    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public LocalStateService(LocalDbContext db, OfficeAccessService officeAccess, SyncRequestDispatcher syncRequestDispatcher, SessionState session)
    {
        _db = db;
        _officeAccess = officeAccess;
        _syncRequestDispatcher = syncRequestDispatcher;
        _session = session;

        _db.SavingChanges += HandleSavingChanges;
        _db.SavedChanges += HandleSavedChanges;
        _db.SaveChangesFailed += HandleSaveChangesFailed;
    }

    private void HandleSavingChanges(object? sender, SavingChangesEventArgs args)
    {
        var pendingSyncEntityTypes = _db.ChangeTracker
            .Entries()
            .Where(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
                entry.Entity is ILocalSyncEntity)
            .Select(entry => entry.Entity.GetType().Name)
            .Distinct()
            .OrderBy(name => name)
            .ToArray();

        _hasPendingSyncEntityChanges = pendingSyncEntityTypes.Length > 0;
        _pendingSyncEntityTypeSummary = pendingSyncEntityTypes.Length == 0
            ? string.Empty
            : string.Join(", ", pendingSyncEntityTypes);
    }

    private void HandleSavedChanges(object? sender, SavedChangesEventArgs args)
    {
        if (_hasPendingSyncEntityChanges && _suppressSyncDispatchCount == 0)
        {
            AppLogger.Info("SYNC", $"로컬 변경으로 동기화 예약: {_pendingSyncEntityTypeSummary}");
            _syncRequestDispatcher.RequestDebouncedSync();
        }

        _hasPendingSyncEntityChanges = false;
        _pendingSyncEntityTypeSummary = string.Empty;
    }

    private void HandleSaveChangesFailed(object? sender, SaveChangesFailedEventArgs args)
    {
        _hasPendingSyncEntityChanges = false;
        _pendingSyncEntityTypeSummary = string.Empty;
    }

    public Task<bool> WaitForServerWriteAsync(CancellationToken ct = default)
    {
        if (!_session.IsLoggedIn || _session.IsOfflineMode)
            return Task.FromResult(false);

        _syncRequestDispatcher.RequestFlushSync();
        return _syncRequestDispatcher.WaitForSyncCompletionAsync(ct);
    }

    public IDisposable SuppressSyncDispatch()
    {
        Interlocked.Increment(ref _suppressSyncDispatchCount);
        return new SyncDispatchSuppressionScope(this);
    }

    private sealed class SyncDispatchSuppressionScope : IDisposable
    {
        private LocalStateService? _owner;

        public SyncDispatchSuppressionScope(LocalStateService owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is not null)
                Interlocked.Decrement(ref owner._suppressSyncDispatchCount);
        }
    }

    private static bool CanModifySharedBusinessData(SessionState? session)
        => session is not null && session.HasAdministrativePrivileges;
    // Customers
    public Task<List<LocalCustomer>> GetCustomersAsync(CancellationToken ct = default)
        => _db.Customers.AsNoTracking().OrderBy(c => c.NameOriginal).ToListAsync(ct);

    public Task<List<LocalCustomer>> GetCustomersAsync(SessionState session, CancellationToken ct = default)
    {
        var query = _db.Customers.AsNoTracking();
        query = ApplyCustomerScope(query, session);
        return query.OrderBy(c => c.NameOriginal).ToListAsync(ct);
    }

    public async Task<Dictionary<Guid, string>> GetCustomerNameMapAsync(
        IEnumerable<Guid> customerIds,
        CancellationToken ct = default)
    {
        var ids = customerIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (ids.Count == 0)
            return new Dictionary<Guid, string>();

        return await _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(customer => ids.Contains(customer.Id))
            .ToDictionaryAsync(
                customer => customer.Id,
                customer => customer.NameOriginal,
                EqualityComparer<Guid>.Default,
                ct);
    }

    public Task<LocalCustomer?> GetCustomerAsync(Guid customerId, CancellationToken ct = default)
        => _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(customer => customer.Id == customerId, ct);

    public async Task<LocalCustomer?> GetCustomerAsync(Guid customerId, SessionState session, CancellationToken ct = default)
    {
        var customer = await GetCustomerAsync(customerId, ct);
        return customer is not null && CanAccessCustomer(customer, session)
            ? customer
            : null;
    }

    public async Task<LocalCustomer> UpsertCustomerAsync(LocalCustomer customer, CancellationToken ct = default)
    {
        customer.ResponsibleOfficeCode = NormalizeOfficeScope(customer.ResponsibleOfficeCode, DomainConstants.OfficeUsenet);
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

    public async Task<OfficeMutationResult> UpsertCustomerAsync(
        LocalCustomer customer,
        SessionState session,
        CancellationToken ct = default)
    {
        if (customer is null)
            throw new ArgumentNullException(nameof(customer));

        if (!CanModifySharedBusinessData(session))
            return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 거래처를 저장할 수 있습니다.");

        var normalizedOfficeCode = NormalizeOfficeScope(customer.ResponsibleOfficeCode, DomainConstants.OfficeUsenet);
        var existing = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == customer.Id, ct);

        if (existing is not null && !CanAccessCustomer(existing, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래처를 저장할 수 없습니다.");

        customer.ResponsibleOfficeCode = normalizedOfficeCode;
        customer.IsDirty = true;
        customer.UpdatedAtUtc = DateTime.UtcNow;

        if (existing is null)
        {
            _db.Customers.Add(customer);
        }
        else
        {
            _db.Entry(existing).CurrentValues.SetValues(customer);
        }

        await _db.SaveChangesAsync(ct);

        var grantedTemporaryAccess = !session.HasAdministrativePrivileges &&
                                     !IsSharedOfficeScope(normalizedOfficeCode) &&
                                     !string.Equals(normalizedOfficeCode, NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet), StringComparison.OrdinalIgnoreCase);

        if (grantedTemporaryAccess)
            _officeAccess.GrantTemporaryCustomerAccess(session, customer.Id);
        else
            _officeAccess.RevokeTemporaryCustomerAccess(session, customer.Id);

        return OfficeMutationResult.Ok(
            customer.Id,
            grantedTemporaryAccess
                ? $"거래처를 저장했습니다. {normalizedOfficeCode} 거래처는 임시 권한으로 계속 작업할 수 있습니다."
                : "거래처를 저장했습니다.",
            grantedTemporaryAccess);
    }

    public async Task DeleteCustomerAsync(Guid id, CancellationToken ct = default)
    {
        var customer = await _db.Customers.FindAsync([id], ct);
        if (customer is null)
            return;

        customer.IsDeleted = true;
        customer.IsDirty = true;
        customer.UpdatedAtUtc = DateTime.UtcNow;
        await SoftDeleteCustomerContractsAsync(id, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<OfficeMutationResult> DeleteCustomerAsync(
        Guid id,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanModifySharedBusinessData(session))
            return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 거래처를 삭제할 수 있습니다.");

        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == id, ct);
        if (customer is null)
            return OfficeMutationResult.Missing("거래처를 찾을 수 없습니다.");

        if (!CanAccessCustomer(customer, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래처를 삭제할 수 없습니다.");

        customer.IsDeleted = true;
        customer.IsDirty = true;
        customer.UpdatedAtUtc = DateTime.UtcNow;
        await SoftDeleteCustomerContractsAsync(id, ct);
        await _db.SaveChangesAsync(ct);
        _officeAccess.RevokeTemporaryCustomerAccess(session, id);

        return OfficeMutationResult.Ok(id, "거래처를 삭제했습니다.");
    }

    public async Task<List<LocalCustomerContract>> GetCustomerContractsAsync(
        Guid customerId,
        SessionState session,
        CancellationToken ct = default)
    {
        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == customerId, ct);
        if (customer is null || !CanAccessCustomer(customer, session))
            return new List<LocalCustomerContract>();

        return await _db.CustomerContracts
            .AsNoTracking()
            .Where(current => current.CustomerId == customerId)
            .OrderByDescending(current => current.IsPrimary)
            .ThenByDescending(current => current.SignedDate)
            .ThenByDescending(current => current.UploadedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<Dictionary<Guid, CustomerContractSummaryItem>> GetCustomerContractSummaryMapAsync(
        SessionState session,
        int alertWindowDays = 30,
        CancellationToken ct = default)
    {
        var customerRows = await ApplyCustomerScope(_db.Customers.AsNoTracking(), session)
            .Select(customer => new { customer.Id, customer.NameOriginal })
            .ToListAsync(ct);

        if (customerRows.Count == 0)
            return new Dictionary<Guid, CustomerContractSummaryItem>();

        var customerIds = customerRows.Select(row => row.Id).ToList();
        var contracts = await _db.CustomerContracts
            .AsNoTracking()
            .Where(contract => customerIds.Contains(contract.CustomerId))
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var alertLimit = today.AddDays(Math.Max(alertWindowDays, 0));
        var contractLookup = contracts
            .GroupBy(contract => contract.CustomerId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var result = new Dictionary<Guid, CustomerContractSummaryItem>();
        foreach (var customerRow in customerRows)
        {
            contractLookup.TryGetValue(customerRow.Id, out var items);
            items ??= new List<LocalCustomerContract>();

            var expiringSoonCount = items.Count(contract =>
                contract.ExpireDate.HasValue &&
                contract.ExpireDate.Value >= today &&
                contract.ExpireDate.Value <= alertLimit);
            var nearestExpireDate = items
                .Where(contract => contract.ExpireDate.HasValue)
                .Select(contract => contract.ExpireDate!.Value)
                .OrderBy(date => date)
                .FirstOrDefault();

            result[customerRow.Id] = new CustomerContractSummaryItem
            {
                CustomerId = customerRow.Id,
                ContractCount = items.Count,
                NearestExpireDate = nearestExpireDate == default ? null : nearestExpireDate,
                ExpiringSoonCount = expiringSoonCount,
                HasExpiredContract = items.Any(contract =>
                    contract.ExpireDate.HasValue &&
                    contract.ExpireDate.Value < today)
            };
        }

        return result;
    }

    public async Task<List<CustomerContractAlertItem>> GetCustomerContractAlertsAsync(
        SessionState session,
        int alertWindowDays = 30,
        CancellationToken ct = default)
    {
        var customerRows = await ApplyCustomerScope(_db.Customers.AsNoTracking(), session)
            .Select(customer => new { customer.Id, customer.NameOriginal })
            .ToListAsync(ct);

        if (customerRows.Count == 0)
            return new List<CustomerContractAlertItem>();

        var customerNameMap = customerRows.ToDictionary(row => row.Id, row => row.NameOriginal);
        var customerIds = customerNameMap.Keys.ToList();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var alertLimit = today.AddDays(Math.Max(alertWindowDays, 0));

        var contracts = await _db.CustomerContracts
            .AsNoTracking()
            .Where(contract =>
                customerIds.Contains(contract.CustomerId) &&
                contract.ExpireDate.HasValue &&
                contract.ExpireDate.Value <= alertLimit)
            .OrderBy(contract => contract.ExpireDate)
            .ToListAsync(ct);

        return contracts
            .Select(contract => new CustomerContractAlertItem
            {
                CustomerId = contract.CustomerId,
                ContractId = contract.Id,
                CustomerName = customerNameMap[contract.CustomerId],
                ContractType = contract.ContractType,
                FileName = contract.FileName,
                ExpireDate = contract.ExpireDate!.Value,
                DaysRemaining = contract.ExpireDate!.Value.DayNumber - today.DayNumber,
                AlertLevel = contract.ExpireDate.Value < today
                    ? "만료"
                    : contract.ExpireDate.Value == today
                        ? "오늘"
                        : "예정",
                AlertText = contract.ExpireDate.Value < today
                    ? $"만료 {today.DayNumber - contract.ExpireDate.Value.DayNumber:N0}일 경과"
                    : contract.ExpireDate.Value == today
                        ? "오늘 만료"
                        : $"{contract.ExpireDate.Value.DayNumber - today.DayNumber:N0}일 남음"
            })
            .ToList();
    }

    public async Task<OfficeMutationResult> SaveCustomerContractAsync(
        Guid customerId,
        string sourceFilePath,
        string contractType,
        DateOnly? signedDate,
        DateOnly? expireDate,
        string? description,
        bool isPrimary,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanModifySharedBusinessData(session))
            return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 계약서를 등록할 수 있습니다.");

        if (customerId == Guid.Empty)
            return OfficeMutationResult.Denied("계약서를 등록할 거래처를 먼저 저장하세요.");

        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            return OfficeMutationResult.Missing("등록할 PDF 파일을 찾을 수 없습니다.");

        var fileInfo = new FileInfo(sourceFilePath);
        if (!string.Equals(fileInfo.Extension, ".pdf", StringComparison.OrdinalIgnoreCase))
            return OfficeMutationResult.Denied("계약서는 PDF 파일만 등록할 수 있습니다.");

        if (fileInfo.Length <= 0)
            return OfficeMutationResult.Denied("빈 PDF 파일은 등록할 수 없습니다.");

        if (fileInfo.Length > MaxCustomerContractFileSizeBytes)
            return OfficeMutationResult.Denied("계약서 PDF는 15MB 이하로 등록해주세요. 스캔 해상도를 낮추거나 PDF를 압축한 뒤 다시 시도하세요.");

        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == customerId, ct);
        if (customer is null)
            return OfficeMutationResult.Missing("거래처를 찾을 수 없습니다.");

        if (!CanAccessCustomer(customer, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래처 계약서를 등록할 수 없습니다.");

        var now = DateTime.UtcNow;
        var fileBytes = await File.ReadAllBytesAsync(sourceFilePath, ct);
        var contract = new LocalCustomerContract
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ContractType = NormalizeCustomerContractType(contractType),
            FileName = fileInfo.Name,
            MimeType = "application/pdf",
            FileSize = fileInfo.Length,
            FileHash = ComputeFileHash(fileBytes),
            Description = (description ?? string.Empty).Trim(),
            SignedDate = signedDate,
            ExpireDate = expireDate,
            IsPrimary = isPrimary,
            UploadedByUsername = session.User?.Username ?? "local-user",
            UploadedAtUtc = now,
            FileContent = fileBytes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsDirty = true
        };

        if (contract.IsPrimary)
            await ClearPrimaryCustomerContractAsync(customerId, exceptContractId: contract.Id, ct);

        _db.CustomerContracts.Add(contract);
        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalCustomerContract),
            EntityId = contract.Id.ToString("D"),
            Action = "Create",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = string.Empty,
            AfterJson = JsonSerializer.Serialize(new
            {
                contract.CustomerId,
                contract.ContractType,
                contract.FileName,
                contract.IsPrimary,
                contract.SignedDate,
                contract.ExpireDate
            }, AuditJsonOptions),
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(contract.Id, "거래처 계약서를 등록했습니다.");
    }

    public async Task<OfficeMutationResult> DeleteCustomerContractAsync(
        Guid contractId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanModifySharedBusinessData(session))
            return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 계약서를 삭제할 수 있습니다.");

        var contract = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contractId, ct);
        if (contract is null)
            return OfficeMutationResult.Missing("삭제할 계약서를 찾을 수 없습니다.");

        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contract.CustomerId, ct);
        if (customer is null)
            return OfficeMutationResult.Missing("계약서와 연결된 거래처를 찾을 수 없습니다.");

        if (!CanAccessCustomer(customer, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래처 계약서를 삭제할 수 없습니다.");

        contract.IsDeleted = true;
        contract.IsDirty = true;
        contract.IsPrimary = false;
        contract.UpdatedAtUtc = DateTime.UtcNow;

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalCustomerContract),
            EntityId = contract.Id.ToString("D"),
            Action = "Delete",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = JsonSerializer.Serialize(new
            {
                contract.CustomerId,
                contract.ContractType,
                contract.FileName
            }, AuditJsonOptions),
            AfterJson = string.Empty,
            CreatedAtUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(contract.Id, "거래처 계약서를 삭제했습니다.");
    }

    public async Task<OfficeMutationResult> SetPrimaryCustomerContractAsync(
        Guid contractId,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanModifySharedBusinessData(session))
            return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 기본 계약서를 변경할 수 있습니다.");

        var contract = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contractId, ct);
        if (contract is null)
            return OfficeMutationResult.Missing("대표로 지정할 계약서를 찾을 수 없습니다.");

        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == contract.CustomerId, ct);
        if (customer is null)
            return OfficeMutationResult.Missing("계약서와 연결된 거래처를 찾을 수 없습니다.");

        if (!CanAccessCustomer(customer, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래처 계약서를 변경할 수 없습니다.");

        await ClearPrimaryCustomerContractAsync(contract.CustomerId, contract.Id, ct);
        contract.IsPrimary = true;
        contract.IsDirty = true;
        contract.UpdatedAtUtc = DateTime.UtcNow;

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalCustomerContract),
            EntityId = contract.Id.ToString("D"),
            Action = "SetPrimary",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = string.Empty,
            AfterJson = JsonSerializer.Serialize(new
            {
                contract.CustomerId,
                contract.ContractType,
                contract.FileName,
                contract.IsPrimary
            }, AuditJsonOptions),
            CreatedAtUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(contract.Id, "대표 계약서로 지정했습니다.");
    }

    // Items
    public Task<List<LocalItem>> GetItemsAsync(CancellationToken ct = default)
        => _db.Items.AsNoTracking().OrderBy(i => i.NameOriginal).ToListAsync(ct);

    public Task<List<LocalItem>> GetItemsAsync(SessionState session, CancellationToken ct = default)
        => _db.Items.AsNoTracking().OrderBy(i => i.NameOriginal).ToListAsync(ct);

    public Task<LocalItem?> GetItemAsync(Guid itemId, CancellationToken ct = default)
        => _db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == itemId, ct);

    public async Task<string> GetLatestPurchaseVendorNameAsync(Guid itemId, CancellationToken ct = default)
    {
        if (itemId == Guid.Empty)
            return string.Empty;

        var latestVendor = await (
                from invoice in _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                join line in _db.InvoiceLines.IgnoreQueryFilters().AsNoTracking()
                    on invoice.Id equals line.InvoiceId
                join customer in _db.Customers.IgnoreQueryFilters().AsNoTracking()
                    on invoice.CustomerId equals customer.Id
                where !invoice.IsDeleted
                      && invoice.IsLatestVersion
                      && invoice.IsConfirmed
                      && !line.IsDeleted
                      && line.ItemId == itemId
                      && (invoice.VoucherType == VoucherType.Purchase || invoice.VoucherType == VoucherType.Procurement)
                orderby invoice.InvoiceDate descending, invoice.UpdatedAtUtc descending
                select customer.NameOriginal)
            .FirstOrDefaultAsync(ct);

        return latestVendor?.Trim() ?? string.Empty;
    }

    public async Task<LocalItem> UpsertItemAsync(LocalItem item, CancellationToken ct = default)
        => await UpsertItemAsync(item, preferredOfficeCode: null, ct);

    public async Task<LocalItem> UpsertItemAsync(LocalItem item, string? preferredOfficeCode, CancellationToken ct = default)
    {
        item.NameOriginal = RentalCatalogValueNormalizer.NormalizeItemNameDisplayName(item.NameOriginal);
        item.NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(item.NameOriginal);
        item.SpecificationOriginal = RentalCatalogValueNormalizer.NormalizeDisplayText(item.SpecificationOriginal);
        item.SpecificationMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(item.SpecificationOriginal);
        item.CategoryName = SelectionOptionDefaults.NormalizeItemCategoryName(item.CategoryName);
        NormalizeItemOperationalState(item);
        item.IsDirty = true;
        item.UpdatedAtUtc = DateTime.UtcNow;

        item.CategoryName = await EnsureItemCategoryOptionExistsAsync(item.CategoryName, ct);

        var existing = await _db.Items.FindAsync([item.Id], ct);
        if (existing is null)
            _db.Items.Add(item);
        else
            _db.Entry(existing).CurrentValues.SetValues(item);

        await SyncItemWarehouseStocksAsync(
            item.Id,
            item.CurrentStock,
            preferredOfficeCode,
            removeStocks: !ItemOperationalPolicy.SupportsInventory(item.TrackingType),
            ct);
        await _db.SaveChangesAsync(ct);
        RaiseInventoryStateChanged();
        return item;
    }

    public async Task DeleteItemAsync(Guid id, CancellationToken ct = default)
    {
        var item = await _db.Items.FindAsync([id], ct);
        if (item is null)
            return;

        item.IsDeleted = true;
        item.IsDirty = true;
        item.UpdatedAtUtc = DateTime.UtcNow;
        await SyncItemWarehouseStocksAsync(id, 0m, preferredOfficeCode: null, removeStocks: true, ct);
        await _db.SaveChangesAsync(ct);
        RaiseInventoryStateChanged();
    }

    // Offices & Warehouses
    public async Task<List<LocalOffice>> GetOfficesAsync(CancellationToken ct = default)
    {
        var offices = await _db.Offices
            .AsNoTracking()
            .Where(office =>
                office.Code == OfficeCodeCatalog.Usenet ||
                office.Code == OfficeCodeCatalog.Itworld ||
                office.Code == OfficeCodeCatalog.Yeonsu)
            .ToListAsync(ct);

        return offices
            .OrderBy(office => GetOfficeSortOrder(office.Code))
            .ThenBy(office => office.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<OfficeMutationResult> SaveOfficeAsync(LocalOffice office, CancellationToken ct = default)
    {
        if (office is null)
            throw new ArgumentNullException(nameof(office));

        if (!OfficeCodeCatalog.TryNormalizeOfficeCode(office.Code, out var code))
            return OfficeMutationResult.Denied("담당지점 코드는 USENET, ITWORLD, YEONSU만 사용할 수 있습니다.");

        var name = OfficeCodeCatalog.GetOfficeDisplayName(code);

        var existing = await _db.Offices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == office.Id, ct);
        var oldCode = existing?.Code ?? string.Empty;

        var duplicated = await _db.Offices
            .IgnoreQueryFilters()
            .AnyAsync(current =>
                current.Id != office.Id &&
                current.Code == code,
                ct);
        if (duplicated)
            return OfficeMutationResult.Denied("동일한 담당지점 코드가 이미 존재합니다.");

        office.Id = office.Id == Guid.Empty ? Guid.NewGuid() : office.Id;
        office.Code = code;
        office.Name = name;
        office.IsDirty = true;
        office.UpdatedAtUtc = DateTime.UtcNow;

        if (existing is null)
        {
            office.IsDeleted = false;
            _db.Offices.Add(office);
        }
        else
        {
            existing.Code = code;
            existing.Name = name;
            existing.IsDeleted = false;
            existing.IsDirty = true;
            existing.UpdatedAtUtc = office.UpdatedAtUtc;
        }

        if (existing is not null &&
            !string.Equals(oldCode, code, StringComparison.OrdinalIgnoreCase))
        {
            await CascadeOfficeCodeAsync(oldCode, code, ct);
        }

        await EnsureSystemOfficeMappingAsync(existing ?? office, ct);
        await _db.SaveChangesAsync(ct);
        await RefreshSystemOfficeCodesAsync(ct);

        return OfficeMutationResult.Ok(
            office.Id,
            existing is null
                ? "담당지점을 추가했습니다."
                : "담당지점을 저장했습니다.");
    }

    public async Task<OfficeMutationResult> DeleteOfficeAsync(Guid officeId, CancellationToken ct = default)
    {
        var office = await _db.Offices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == officeId, ct);
        if (office is null)
            return OfficeMutationResult.Missing("담당지점을 찾을 수 없습니다.");

        if (IsSystemOfficeCode(office.Code))
            return OfficeMutationResult.Denied("기본 담당지점은 삭제할 수 없습니다.");

        var officeCode = NormalizeOfficeCode(office.Code, string.Empty);
        var isInUse =
            await _db.Customers.IgnoreQueryFilters().AnyAsync(customer => customer.ResponsibleOfficeCode == officeCode, ct) ||
            await _db.Invoices.IgnoreQueryFilters().AnyAsync(invoice => invoice.ResponsibleOfficeCode == officeCode, ct) ||
            await _db.Transactions.IgnoreQueryFilters().AnyAsync(transaction => transaction.ResponsibleOfficeCode == officeCode, ct) ||
            await _db.Warehouses.IgnoreQueryFilters().AnyAsync(warehouse => warehouse.OfficeCode == officeCode, ct);

        if (isInUse)
            return OfficeMutationResult.Denied("사용 중인 담당지점은 삭제할 수 없습니다.");

        office.IsDeleted = true;
        office.IsDirty = true;
        office.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(office.Id, "담당지점을 삭제했습니다.");
    }

    public Task<List<LocalWarehouse>> GetWarehousesAsync(bool onlyActive = true, CancellationToken ct = default)
    {
        var query = _db.Warehouses.AsNoTracking().Where(warehouse =>
            warehouse.Code == DomainConstants.WarehouseUsenetMain ||
            warehouse.Code == DomainConstants.WarehouseItworldMain ||
            warehouse.Code == DomainConstants.WarehouseYeonsuMain);
        if (onlyActive)
            query = query.Where(w => w.IsActive);

        return query.OrderBy(w => w.OfficeCode).ThenBy(w => w.Name).ToListAsync(ct);
    }

    public Task<List<LocalWarehouse>> GetWarehousesByOfficeAsync(string officeCode, bool onlyActive = true, CancellationToken ct = default)
    {
        var normalizedOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet);
        var query = _db.Warehouses.AsNoTracking()
            .Where(w => w.OfficeCode == normalizedOfficeCode)
            .Where(warehouse =>
                warehouse.Code == DomainConstants.WarehouseUsenetMain ||
                warehouse.Code == DomainConstants.WarehouseItworldMain ||
                warehouse.Code == DomainConstants.WarehouseYeonsuMain);
        if (onlyActive)
            query = query.Where(w => w.IsActive);

        return query.OrderBy(w => w.Name).ToListAsync(ct);
    }

    private async Task CascadeOfficeCodeAsync(string oldCode, string newCode, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(oldCode) || string.Equals(oldCode, newCode, StringComparison.OrdinalIgnoreCase))
            return;

        var customers = await _db.Customers.IgnoreQueryFilters()
            .Where(customer => customer.ResponsibleOfficeCode == oldCode)
            .ToListAsync(ct);
        foreach (var customer in customers)
        {
            customer.ResponsibleOfficeCode = newCode;
            customer.IsDirty = true;
            customer.UpdatedAtUtc = DateTime.UtcNow;
        }

        var invoices = await _db.Invoices.IgnoreQueryFilters()
            .Where(invoice => invoice.ResponsibleOfficeCode == oldCode)
            .ToListAsync(ct);
        foreach (var invoice in invoices)
        {
            invoice.ResponsibleOfficeCode = newCode;
            invoice.IsDirty = true;
            invoice.UpdatedAtUtc = DateTime.UtcNow;
        }

        var transactions = await _db.Transactions.IgnoreQueryFilters()
            .Where(transaction => transaction.ResponsibleOfficeCode == oldCode)
            .ToListAsync(ct);
        foreach (var transaction in transactions)
        {
            transaction.ResponsibleOfficeCode = newCode;
            transaction.IsDirty = true;
            transaction.UpdatedAtUtc = DateTime.UtcNow;
        }

        var warehouses = await _db.Warehouses.IgnoreQueryFilters()
            .Where(warehouse => warehouse.OfficeCode == oldCode)
            .ToListAsync(ct);
        foreach (var warehouse in warehouses)
        {
            warehouse.OfficeCode = newCode;
            warehouse.IsDirty = true;
            warehouse.UpdatedAtUtc = DateTime.UtcNow;
        }

        var companyProfiles = await _db.CompanyProfiles.IgnoreQueryFilters()
            .Where(profile => profile.OfficeCode == oldCode)
            .ToListAsync(ct);
        foreach (var profile in companyProfiles)
        {
            profile.OfficeCode = newCode;
            profile.IsDirty = true;
            profile.UpdatedAtUtc = DateTime.UtcNow;
        }

        var rentalCompanies = await _db.RentalManagementCompanies.IgnoreQueryFilters()
            .Where(company => company.Code == oldCode)
            .ToListAsync(ct);
        foreach (var company in rentalCompanies)
        {
            company.Code = newCode;
            company.Name = OfficeCodeCatalog.GetOfficeDisplayName(newCode);
            company.IsDirty = true;
            company.UpdatedAtUtc = DateTime.UtcNow;
        }

        var rentalProfiles = await _db.RentalBillingProfiles.IgnoreQueryFilters()
            .Where(profile => profile.ResponsibleOfficeCode == oldCode || profile.ManagementCompanyCode == oldCode)
            .ToListAsync(ct);
        foreach (var profile in rentalProfiles)
        {
            profile.ResponsibleOfficeCode = string.Equals(profile.ResponsibleOfficeCode, oldCode, StringComparison.OrdinalIgnoreCase)
                ? newCode
                : profile.ResponsibleOfficeCode;
            profile.ManagementCompanyCode = string.Equals(profile.ManagementCompanyCode, oldCode, StringComparison.OrdinalIgnoreCase)
                ? newCode
                : profile.ManagementCompanyCode;
            profile.IsDirty = true;
            profile.UpdatedAtUtc = DateTime.UtcNow;
        }

        var rentalAssets = await _db.RentalAssets.IgnoreQueryFilters()
            .Where(asset => asset.ResponsibleOfficeCode == oldCode || asset.ManagementCompanyCode == oldCode)
            .ToListAsync(ct);
        foreach (var asset in rentalAssets)
        {
            asset.ResponsibleOfficeCode = string.Equals(asset.ResponsibleOfficeCode, oldCode, StringComparison.OrdinalIgnoreCase)
                ? newCode
                : asset.ResponsibleOfficeCode;
            asset.ManagementCompanyCode = string.Equals(asset.ManagementCompanyCode, oldCode, StringComparison.OrdinalIgnoreCase)
                ? newCode
                : asset.ManagementCompanyCode;
            asset.IsDirty = true;
            asset.UpdatedAtUtc = DateTime.UtcNow;
        }

        var rentalLogs = await _db.RentalBillingLogs.IgnoreQueryFilters()
            .Where(log => log.ResponsibleOfficeCode == oldCode)
            .ToListAsync(ct);
        foreach (var log in rentalLogs)
        {
            log.ResponsibleOfficeCode = newCode;
            log.IsDirty = true;
            log.UpdatedAtUtc = DateTime.UtcNow;
        }

        var audits = await _db.AuditLogs
            .Where(log => log.OfficeCode == oldCode)
            .ToListAsync(ct);
        foreach (var audit in audits)
        {
            audit.OfficeCode = newCode;
            audit.CreatedAtUtc = DateTime.UtcNow;
        }

        var cachedOfficeCode = await GetSettingAsync("CachedSession_OfficeCode", ct);
        if (string.Equals(cachedOfficeCode, oldCode, StringComparison.OrdinalIgnoreCase))
            await SetSettingAsync("CachedSession_OfficeCode", newCode, ct);
    }

    private async Task EnsureSystemOfficeMappingAsync(LocalOffice office, CancellationToken ct)
    {
        if (office.IsHeadOffice)
            return;

        var setting = await _db.Settings.FirstOrDefaultAsync(current => current.Key == YeonsuOfficeIdSettingKey, ct);
        if (setting is null)
        {
            _db.Settings.Add(new LocalSetting
            {
                Key = YeonsuOfficeIdSettingKey,
                Value = office.Id.ToString("D")
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(setting.Value))
            setting.Value = office.Id.ToString("D");
    }

    private async Task RefreshSystemOfficeCodesAsync(CancellationToken ct)
    {
        var offices = await _db.Offices
            .IgnoreQueryFilters()
            .Where(office => !office.IsDeleted)
            .AsNoTracking()
            .ToListAsync(ct);

        var headOffice = offices.FirstOrDefault(office => string.Equals(office.Code, DomainConstants.OfficeUsenet, StringComparison.OrdinalIgnoreCase))
                         ?? offices.FirstOrDefault();

        var yeonsuOfficeIdSetting = await _db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(setting => setting.Key == YeonsuOfficeIdSettingKey, ct);

        LocalOffice? yeonsuOffice = null;
        if (yeonsuOfficeIdSetting is not null && Guid.TryParse(yeonsuOfficeIdSetting.Value, out var yeonsuOfficeId))
            yeonsuOffice = offices.FirstOrDefault(office => office.Id == yeonsuOfficeId);

        yeonsuOffice ??= offices.FirstOrDefault(office => string.Equals(office.Code, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase))
                         ?? offices.FirstOrDefault(office => office.Id != headOffice?.Id);

        var itworldOffice = offices.FirstOrDefault(office => string.Equals(office.Code, DomainConstants.OfficeItworld, StringComparison.OrdinalIgnoreCase));

        DomainConstants.ConfigureSystemOffices(
            headOffice?.Code,
            yeonsuOffice?.Code,
            DomainConstants.WarehouseUsenetMain,
            DomainConstants.WarehouseYeonsuMain,
            itworldOffice?.Code,
            DomainConstants.WarehouseItworldMain);
    }

    // Invoices
    public Task<List<LocalInvoice>> GetInvoicesAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? customerId = null,
        CancellationToken ct = default)
        => GetInvoicesWithOptionsAsync(from, to, customerId, latestOnly: true, ct);

    public Task<List<LocalInvoice>> GetInvoicesAsync(
        DateOnly? from,
        DateOnly? to,
        Guid? customerId,
        SessionState session,
        CancellationToken ct = default)
        => GetInvoicesWithOptionsAsync(from, to, customerId, latestOnly: true, session, ct);

    public Task<List<LocalInvoice>> GetInvoicesWithOptionsAsync(
        DateOnly? from,
        DateOnly? to,
        Guid? customerId,
        bool latestOnly,
        CancellationToken ct = default)
    {
        var query = _db.Invoices
            .Include(i => i.Lines.Where(l => !l.IsDeleted))
            .Include(i => i.Payments.Where(p => !p.IsDeleted))
            .AsNoTracking();

        if (latestOnly)
            query = query.Where(i => i.IsLatestVersion);

        if (from.HasValue)
            query = query.Where(i => i.InvoiceDate >= from.Value);

        if (to.HasValue)
            query = query.Where(i => i.InvoiceDate <= to.Value);

        if (customerId.HasValue)
            query = query.Where(i => i.CustomerId == customerId.Value);

        return query.OrderByDescending(i => i.InvoiceDate)
            .ThenByDescending(i => i.UpdatedAtUtc)
            .ThenByDescending(i => i.VersionNumber)
            .ToListAsync(ct);
    }

    public Task<List<LocalInvoice>> GetInvoicesWithOptionsAsync(
        DateOnly? from,
        DateOnly? to,
        Guid? customerId,
        bool latestOnly,
        SessionState session,
        CancellationToken ct = default)
    {
        var query = _db.Invoices
            .Include(i => i.Lines.Where(l => !l.IsDeleted))
            .Include(i => i.Payments.Where(p => !p.IsDeleted))
            .AsNoTracking();

        query = ApplyInvoiceScope(query, session);

        if (latestOnly)
            query = query.Where(i => i.IsLatestVersion);

        if (from.HasValue)
            query = query.Where(i => i.InvoiceDate >= from.Value);

        if (to.HasValue)
            query = query.Where(i => i.InvoiceDate <= to.Value);

        if (customerId.HasValue)
            query = query.Where(i => i.CustomerId == customerId.Value);

        return query.OrderByDescending(i => i.InvoiceDate)
            .ThenByDescending(i => i.UpdatedAtUtc)
            .ThenByDescending(i => i.VersionNumber)
            .ToListAsync(ct);
    }

    public async Task<List<LocalInvoice>> GetYeonsuDeliveryInvoicesAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? customerId = null,
        string? warehouseCode = null,
        string? responsibleOfficeCode = null,
        CancellationToken ct = default)
    {
        var normalizedOfficeCode = NormalizeOfficeCode(responsibleOfficeCode, DomainConstants.OfficeYeonsu);
        var query = _db.Invoices
            .Include(invoice => invoice.Lines.Where(line => !line.IsDeleted))
            .Include(invoice => invoice.Payments.Where(payment => !payment.IsDeleted))
            .AsNoTracking()
            .Where(invoice => !invoice.IsDeleted &&
                              invoice.IsLatestVersion &&
                              invoice.IsConfirmed &&
                              invoice.VoucherType == VoucherType.Sales &&
                              invoice.ResponsibleOfficeCode == normalizedOfficeCode);

        if (from.HasValue)
            query = query.Where(invoice => invoice.InvoiceDate >= from.Value);

        if (to.HasValue)
            query = query.Where(invoice => invoice.InvoiceDate <= to.Value);

        if (customerId.HasValue)
            query = query.Where(invoice => invoice.CustomerId == customerId.Value);

        var normalizedWarehouseCode = (warehouseCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedWarehouseCode))
            query = query.Where(invoice => invoice.SourceWarehouseCode == normalizedWarehouseCode);

        return await query
            .OrderByDescending(invoice => invoice.InvoiceDate)
            .ThenByDescending(invoice => invoice.LastSavedAtUtc)
            .ThenByDescending(invoice => invoice.UpdatedAtUtc)
            .ToListAsync(ct);
    }

    public Task<List<LocalInvoice>> GetYeonsuDeliveryInvoicesAsync(
        DateOnly? from,
        DateOnly? to,
        Guid? customerId,
        string? warehouseCode,
        string? responsibleOfficeCode,
        SessionState session,
        CancellationToken ct = default)
    {
        var officeCode = session.HasGlobalDataScope
            ? NormalizeOfficeCode(responsibleOfficeCode, DomainConstants.OfficeYeonsu)
            : NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeYeonsu);

        return GetYeonsuDeliveryInvoicesAsync(from, to, customerId, warehouseCode, officeCode, ct);
    }

    public async Task<LocalInvoice?> GetInvoiceAsync(Guid id, CancellationToken ct = default)
        => await _db.Invoices
            .Include(i => i.Lines.Where(l => !l.IsDeleted))
            .Include(i => i.Payments.Where(p => !p.IsDeleted))
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, ct);

    public async Task<LocalInvoice?> GetInvoiceAsync(Guid id, SessionState session, CancellationToken ct = default)
    {
        var invoice = await GetInvoiceAsync(id, ct);
        return invoice is not null && CanAccessInvoice(invoice, session)
            ? invoice
            : null;
    }

    public async Task<List<LocalInvoice>> GetInvoiceVersionsAsync(Guid invoiceIdOrVersionGroupId, CancellationToken ct = default)
    {
        var versionGroupId = invoiceIdOrVersionGroupId;

        var invoice = await _db.Invoices
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == invoiceIdOrVersionGroupId, ct);

        if (invoice is not null)
        {
            versionGroupId = invoice.VersionGroupId == Guid.Empty
                ? invoice.Id
                : invoice.VersionGroupId;
        }

        return await _db.Invoices
            .Include(i => i.Lines.Where(l => !l.IsDeleted))
            .Include(i => i.Payments.Where(p => !p.IsDeleted))
            .AsNoTracking()
            .Where(i => i.VersionGroupId == versionGroupId || (i.VersionGroupId == Guid.Empty && i.Id == versionGroupId))
            .OrderByDescending(i => i.VersionNumber)
            .ThenByDescending(i => i.UpdatedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<List<LocalInvoice>> GetInvoiceVersionsAsync(
        Guid invoiceIdOrVersionGroupId,
        SessionState session,
        CancellationToken ct = default)
    {
        var versions = await GetInvoiceVersionsAsync(invoiceIdOrVersionGroupId, ct);
        return versions.Where(version => CanAccessInvoice(version, session)).ToList();
    }

    public async Task<LocalInvoice> SaveInvoiceAsync(LocalInvoice invoice, CancellationToken ct = default)
    {
        var context = new InvoiceSaveContext
        {
            Username = "system",
            Role = DomainConstants.RoleAdmin,
            OfficeCode = DomainConstants.OfficeUsenet,
            ForceOverride = true
        };

        var result = await SaveInvoiceAsync(invoice, context, session: null, ct);
        if (!result.Success)
            throw new InvalidOperationException(result.Message);

        var saved = await GetInvoiceAsync(result.SavedInvoiceId, ct);
        if (saved is null)
            throw new InvalidOperationException("저장한 전표를 다시 불러올 수 없습니다.");

        return saved;
    }

    public async Task<InvoiceSaveResult> SaveInvoiceAsync(
        LocalInvoice invoice,
        InvoiceSaveContext saveContext,
        SessionState? session = null,
        CancellationToken ct = default)
    {
        if (invoice is null)
            throw new ArgumentNullException(nameof(invoice));

        if (session is not null && !CanModifySharedBusinessData(session))
            return InvoiceSaveResult.Denied("관리자 또는 god 권한 계정만 전표를 저장할 수 있습니다.");

        var context = NormalizeSaveContext(saveContext);
        var now = DateTime.UtcNow;

        var customer = await _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == invoice.CustomerId, ct);

        if (customer is null)
            return InvoiceSaveResult.Missing("거래처 정보를 찾을 수 없습니다.");

        var customerOfficeCode = NormalizeOfficeScope(customer.ResponsibleOfficeCode, DomainConstants.OfficeUsenet);
        if (!CanAccessCustomer(customer.Id, customerOfficeCode, session, context.Role))
            return InvoiceSaveResult.Denied("권한이 없어 해당 거래처 전표를 저장할 수 없습니다.");

        var latest = await ResolveLatestVersionAsync(invoice, ct);
        if (latest is not null && !CanAccessInvoice(latest, session))
            return InvoiceSaveResult.Denied("권한이 없어 해당 거래처 전표를 저장할 수 없습니다.");

        if (latest is not null &&
            !context.ForceOverride &&
            !string.IsNullOrWhiteSpace(context.ExpectedConcurrencyStamp) &&
            !string.Equals(context.ExpectedConcurrencyStamp, latest.ConcurrencyStamp, StringComparison.OrdinalIgnoreCase))
        {
            return InvoiceSaveResult.Conflict("다른 사용자가 먼저 저장했습니다. 최신 전표를 다시 불러온 뒤 다시 시도하세요.");
        }

        var versionGroupId = ResolveVersionGroupId(invoice, latest);
        if (latest is not null && latest.VersionGroupId == Guid.Empty)
            latest.VersionGroupId = versionGroupId;

        var targetInvoiceId = latest is null
            ? (invoice.Id == Guid.Empty ? Guid.NewGuid() : invoice.Id)
            : Guid.NewGuid();

        var versionNumber = (latest?.VersionNumber ?? 0) + 1;

        var responsibleOfficeCode = NormalizeOfficeScope(
            string.IsNullOrWhiteSpace(invoice.ResponsibleOfficeCode)
                ? latest?.ResponsibleOfficeCode
                : invoice.ResponsibleOfficeCode,
            customerOfficeCode);

        var warehouseOfficeCode = IsSharedOfficeScope(responsibleOfficeCode)
            ? NormalizeOfficeCode(context.OfficeCode, DomainConstants.OfficeUsenet)
            : NormalizeOfficeCode(responsibleOfficeCode, customerOfficeCode);

        var sourceWarehouseCode = NormalizeWarehouseCode(
            invoice.SourceWarehouseCode,
            warehouseOfficeCode,
            warehouseOfficeCode);

        var validLines = (invoice.Lines ?? new List<LocalInvoiceLine>())
            .Where(line => !line.IsDeleted && !string.IsNullOrWhiteSpace(line.ItemNameOriginal))
            .ToList();

        var itemTrackingMap = await BuildItemTrackingMapAsync(ct);
        foreach (var line in validLines)
            line.ItemTrackingType = ResolveInvoiceLineTrackingType(line, itemTrackingMap);

        var totalAmount = validLines.Sum(line => line.LineAmount);
        var supplyAmount = Math.Round(totalAmount / 1.1m, 0, MidpointRounding.AwayFromZero);
        var vatAmount = totalAmount - supplyAmount;

        var newInvoice = new LocalInvoice
        {
            Id = targetInvoiceId,
            CustomerId = invoice.CustomerId,
            InvoiceNumber = !string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
                ? invoice.InvoiceNumber
                : latest?.InvoiceNumber ?? string.Empty,
            LocalTempNumber = !string.IsNullOrWhiteSpace(invoice.LocalTempNumber)
                ? invoice.LocalTempNumber
                : latest?.LocalTempNumber ?? string.Empty,
            VoucherType = invoice.VoucherType,
            InvoiceDate = invoice.InvoiceDate,
            TotalAmount = totalAmount,
            SupplyAmount = supplyAmount,
            VatAmount = vatAmount,
            Memo = invoice.Memo ?? string.Empty,
            ResponsibleOfficeCode = responsibleOfficeCode,
            SourceWarehouseCode = sourceWarehouseCode,
            DeliveryGroupId = invoice.DeliveryGroupId,
            ParentInvoiceId = invoice.ParentInvoiceId,
            VersionGroupId = versionGroupId,
            VersionNumber = versionNumber,
            PreviousVersionId = latest?.Id,
            IsLatestVersion = true,
            IsConfirmed = true,
            CreatedByUsername = string.IsNullOrWhiteSpace(latest?.CreatedByUsername)
                ? context.Username
                : latest.CreatedByUsername,
            LastSavedByUsername = context.Username,
            LastSavedAtUtc = now,
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CostStatus = "Pending",
            IsDirty = true,
            UpdatedAtUtc = now,
            CreatedAtUtc = latest?.CreatedAtUtc ?? now,
            Revision = latest?.Revision ?? 0
        };

        if (string.IsNullOrWhiteSpace(newInvoice.LocalTempNumber))
            newInvoice.LocalTempNumber = await GenerateLocalTempNumberAsync(newInvoice.InvoiceDate, ct);

        newInvoice.Lines = CloneLines(validLines, targetInvoiceId);

        var requestedPayments = (invoice.Payments ?? new List<LocalPayment>())
            .Where(p => !p.IsDeleted)
            .ToList();

        if (requestedPayments.Count > 0)
        {
            newInvoice.Payments = ClonePayments(requestedPayments, targetInvoiceId, now);
        }
        else if (latest is not null)
        {
            var latestPayments = latest.Payments
                .Where(payment => !payment.IsDeleted)
                .ToList();
            newInvoice.Payments = ClonePayments(latestPayments, targetInvoiceId, now);
        }

        if (latest is not null)
        {
            latest.IsLatestVersion = false;
            latest.IsDirty = true;
            latest.UpdatedAtUtc = now;
            latest.LastSavedAtUtc = now;
        }

        var beforeJson = latest is null ? string.Empty : JsonSerializer.Serialize(BuildAuditInvoice(latest), AuditJsonOptions);
        var afterJson = JsonSerializer.Serialize(BuildAuditInvoice(newInvoice), AuditJsonOptions);

        _db.Invoices.Add(newInvoice);
        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalInvoice),
            EntityId = newInvoice.Id.ToString("D"),
            Action = latest is null ? "Create" : "Revise",
            Username = context.Username,
            Role = context.Role,
            OfficeCode = context.OfficeCode,
            BeforeJson = beforeJson,
            AfterJson = afterJson,
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);
        await RebuildInventorySnapshotsAsync(context, ct);

        return InvoiceSaveResult.Ok(
            newInvoice.Id,
            newInvoice.ConcurrencyStamp,
            latest is null ? "전표를 저장했습니다." : $"전표 {newInvoice.VersionNumber}차 버전으로 저장했습니다.");
    }

    public async Task DeleteInvoiceAsync(Guid id, CancellationToken ct = default)
    {
        var target = await _db.Invoices
            .FirstOrDefaultAsync(invoice => invoice.Id == id, ct);

        if (target is null)
            return;

        var now = DateTime.UtcNow;
        var versionGroupId = target.VersionGroupId == Guid.Empty ? target.Id : target.VersionGroupId;

        var invoicesToDelete = await _db.Invoices
            .Where(invoice => invoice.Id == id || invoice.VersionGroupId == versionGroupId)
            .ToListAsync(ct);

        foreach (var invoice in invoicesToDelete)
        {
            invoice.IsDeleted = true;
            invoice.IsLatestVersion = false;
            invoice.IsDirty = true;
            invoice.UpdatedAtUtc = now;
            invoice.LastSavedAtUtc = now;
        }

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalInvoice),
            EntityId = id.ToString("D"),
            Action = "Delete",
            Username = "system",
            Role = DomainConstants.RoleAdmin,
            OfficeCode = DomainConstants.OfficeUsenet,
            BeforeJson = string.Empty,
            AfterJson = string.Empty,
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);

        await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
        {
            Username = "system",
            Role = DomainConstants.RoleAdmin,
            OfficeCode = DomainConstants.OfficeUsenet,
            ForceOverride = true
        }, ct);
    }

    public async Task<OfficeMutationResult> DeleteInvoiceAsync(
        Guid id,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanModifySharedBusinessData(session))
            return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 전표를 삭제할 수 있습니다.");

        var target = await _db.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(invoice => invoice.Id == id, ct);

        if (target is null)
            return OfficeMutationResult.Missing("전표를 찾을 수 없습니다.");

        if (!CanAccessInvoice(target, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 전표를 삭제할 수 없습니다.");

        var now = DateTime.UtcNow;
        var versionGroupId = target.VersionGroupId == Guid.Empty ? target.Id : target.VersionGroupId;

        var invoicesToDelete = await _db.Invoices
            .IgnoreQueryFilters()
            .Where(invoice => invoice.Id == id || invoice.VersionGroupId == versionGroupId)
            .ToListAsync(ct);

        foreach (var invoice in invoicesToDelete)
        {
            invoice.IsDeleted = true;
            invoice.IsLatestVersion = false;
            invoice.IsDirty = true;
            invoice.UpdatedAtUtc = now;
            invoice.LastSavedAtUtc = now;
        }

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalInvoice),
            EntityId = id.ToString("D"),
            Action = "Delete",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = string.Empty,
            AfterJson = string.Empty,
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);

        await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
        {
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            ForceOverride = false
        }, ct);

        return OfficeMutationResult.Ok(id, "전표를 삭제했습니다.");
    }

    // Payments
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

    public async Task<OfficeMutationResult> SavePaymentAsync(
        LocalPayment payment,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanModifySharedBusinessData(session))
            return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 전표 결제를 저장할 수 있습니다.");

        var invoice = await _db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == payment.InvoiceId, ct);
        if (invoice is null)
            return OfficeMutationResult.Missing("전표를 찾을 수 없습니다.");

        if (!CanAccessInvoice(invoice, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 전표 수금/지급을 저장할 수 없습니다.");

        payment.IsDirty = true;
        payment.UpdatedAtUtc = DateTime.UtcNow;

        var existing = await _db.Payments.FindAsync([payment.Id], ct);
        if (existing is null)
            _db.Payments.Add(payment);
        else
            _db.Entry(existing).CurrentValues.SetValues(payment);

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(payment.Id, "수금/지급을 저장했습니다.");
    }

    public async Task DeletePaymentAsync(Guid id, CancellationToken ct = default)
    {
        var payment = await _db.Payments.FindAsync([id], ct);
        if (payment is null)
            return;

        payment.IsDeleted = true;
        payment.IsDirty = true;
        payment.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // CompanyProfile
    public Task<LocalCompanyProfile?> GetCompanyProfileAsync(CancellationToken ct = default)
        => GetCompanyProfileAsync(username: null, officeCode: null, ct);

    public Task<LocalCompanyProfile?> GetCompanyProfileAsync(SessionState session, CancellationToken ct = default)
        => GetCompanyProfileAsync(session.User?.Username, session.BusinessOfficeCode, ct);

    public Task<List<LocalCompanyProfile>> GetCompanyProfilesAsync(CancellationToken ct = default)
        => _db.CompanyProfiles
            .AsNoTracking()
            .Where(profile => profile.IsActive)
            .OrderBy(profile => profile.OfficeCode)
            .ThenByDescending(profile => profile.IsDefaultForOffice)
            .ThenBy(profile => profile.ProfileName)
            .ToListAsync(ct);

    public async Task<LocalCompanyProfile?> GetCompanyProfileAsync(
        string? username,
        string? officeCode,
        CancellationToken ct = default)
    {
        var profiles = await GetCompanyProfilesAsync(ct);
        if (profiles.Count == 0)
            return null;

        var normalizedOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet);
        var assignedId = await GetAssignedCompanyProfileIdAsync(username, ct);
        if (assignedId.HasValue)
        {
            var assigned = profiles.FirstOrDefault(profile => profile.Id == assignedId.Value);
            if (assigned is not null)
                return assigned;
        }

        var officeDefault = profiles.FirstOrDefault(profile =>
            string.Equals(profile.OfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase) &&
            profile.IsDefaultForOffice);
        if (officeDefault is not null)
            return officeDefault;

        var officeMatch = profiles.FirstOrDefault(profile =>
            string.Equals(profile.OfficeCode, normalizedOfficeCode, StringComparison.OrdinalIgnoreCase));
        if (officeMatch is not null)
            return officeMatch;

        return profiles[0];
    }

    public async Task SaveCompanyProfileAsync(LocalCompanyProfile profile, CancellationToken ct = default)
    {
        profile.ProfileName = string.IsNullOrWhiteSpace(profile.ProfileName)
            ? (string.IsNullOrWhiteSpace(profile.TradeName) ? "회사설정" : $"{profile.TradeName.Trim()} 설정")
            : profile.ProfileName.Trim();
        profile.OfficeCode = NormalizeOfficeCode(profile.OfficeCode, DomainConstants.OfficeUsenet);
        profile.TradeName = (profile.TradeName ?? string.Empty).Trim();
        profile.Representative = (profile.Representative ?? string.Empty).Trim();
        profile.BusinessNumber = (profile.BusinessNumber ?? string.Empty).Trim();
        profile.BusinessType = (profile.BusinessType ?? string.Empty).Trim();
        profile.BusinessItem = (profile.BusinessItem ?? string.Empty).Trim();
        profile.Address = (profile.Address ?? string.Empty).Trim();
        profile.ContactNumber = (profile.ContactNumber ?? string.Empty).Trim();
        profile.Email = (profile.Email ?? string.Empty).Trim();
        profile.BankAccountText = profile.BankAccountText ?? string.Empty;
        profile.IsActive = true;
        profile.IsDirty = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;

        var existing = await _db.CompanyProfiles.FindAsync([profile.Id], ct);
        if (existing is null)
            _db.CompanyProfiles.Add(profile);
        else
            _db.Entry(existing).CurrentValues.SetValues(profile);

        if (profile.IsDefaultForOffice)
        {
            var others = await _db.CompanyProfiles
                .IgnoreQueryFilters()
                .Where(current =>
                    current.Id != profile.Id &&
                    !current.IsDeleted &&
                    current.IsActive &&
                    current.IsDefaultForOffice &&
                    current.OfficeCode == profile.OfficeCode)
                .ToListAsync(ct);
            foreach (var other in others)
            {
                other.IsDefaultForOffice = false;
                other.IsDirty = true;
                other.UpdatedAtUtc = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<LocalMutationResult> DeleteCompanyProfileAsync(Guid profileId, CancellationToken ct = default)
    {
        var profile = await _db.CompanyProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == profileId, ct);
        if (profile is null)
            return LocalMutationResult.Missing("회사설정을 찾을 수 없습니다.");

        var assignedKeys = await _db.Settings
            .AsNoTracking()
            .Where(setting => setting.Key.StartsWith(CompanyProfileAssignmentPrefix) && setting.Value == profileId.ToString("D"))
            .ToListAsync(ct);
        var assignedUsers = assignedKeys
            .Select(setting => setting.Key.Substring(CompanyProfileAssignmentPrefix.Length))
            .ToList();
        if (assignedUsers.Count > 0)
            return LocalMutationResult.Denied($"해당 회사설정을 사용하는 사용자({string.Join(", ", assignedUsers)})를 먼저 변경하세요.");

        profile.IsDeleted = true;
        profile.IsActive = false;
        profile.IsDirty = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(profileId, "회사설정을 삭제했습니다.");
    }

    public async Task<Guid?> GetAssignedCompanyProfileIdAsync(string? username, CancellationToken ct = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername))
            return null;

        var rawValue = await GetSettingAsync(GetCompanyProfileAssignmentKey(normalizedUsername), ct);
        return Guid.TryParse(rawValue, out var profileId)
            ? profileId
            : null;
    }

    public async Task SetAssignedCompanyProfileAsync(string? username, Guid? profileId, CancellationToken ct = default)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(normalizedUsername))
            return;

        var value = profileId.HasValue && profileId.Value != Guid.Empty
            ? profileId.Value.ToString("D")
            : string.Empty;
        await SetSettingAsync(GetCompanyProfileAssignmentKey(normalizedUsername), value, ct);
    }

    // Units & Categories
    public Task<List<LocalUnit>> GetUnitsAsync(CancellationToken ct = default)
        => _db.Units.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.Name).ToListAsync(ct);

    public async Task EnsureCustomerCategoryIntegrityAsync(CancellationToken ct = default)
        => await CustomerCategoryMaintenance.NormalizeAsync(_db, ct);

    public async Task<List<LocalCustomerCategory>> GetCategoriesAsync(CancellationToken ct = default)
    {
        await EnsureCustomerCategoryIntegrityAsync(ct);

        return await _db.CustomerCategories
            .AsNoTracking()
            .OrderBy(c => c.Name)
            .ThenBy(c => c.CreatedAtUtc)
            .ToListAsync(ct);
    }

    public Task<List<LocalPriceGradeOption>> GetPriceGradeOptionsAsync(CancellationToken ct = default)
        => _db.PriceGradeOptions
            .AsNoTracking()
            .Where(option => option.IsActive)
            .OrderBy(option => option.SortOrder)
            .ThenBy(option => option.Name)
            .ToListAsync(ct);

    public Task<List<LocalTradeTypeOption>> GetTradeTypeOptionsAsync(CancellationToken ct = default)
        => _db.TradeTypeOptions
            .AsNoTracking()
            .Where(option => option.IsActive)
            .OrderBy(option => option.SortOrder)
            .ThenBy(option => option.Name)
            .ToListAsync(ct);

    public Task<List<LocalItemCategoryOption>> GetItemCategoryOptionsAsync(CancellationToken ct = default)
        => _db.ItemCategoryOptions
            .AsNoTracking()
            .Where(option => option.IsActive)
            .OrderBy(option => option.SortOrder)
            .ThenBy(option => option.Name)
            .ToListAsync(ct);

    public async Task<LocalMutationResult> SaveCustomerCategoryAsync(LocalCustomerCategory category, CancellationToken ct = default)
    {
        if (category is null)
            throw new ArgumentNullException(nameof(category));

        await EnsureCustomerCategoryIntegrityAsync(ct);

        var name = DefaultCustomerCategories.NormalizeName(category.Name);
        if (string.IsNullOrWhiteSpace(name))
            return LocalMutationResult.Denied("고객분류 이름을 입력하세요.");

        var categories = await _db.CustomerCategories.IgnoreQueryFilters().ToListAsync(ct);
        if (categories.Any(current =>
                current.Id != category.Id &&
                !current.IsDeleted &&
                string.Equals(DefaultCustomerCategories.NormalizeName(current.Name), name, StringComparison.CurrentCultureIgnoreCase)))
        {
            return LocalMutationResult.Denied("같은 이름의 고객분류가 이미 있습니다.");
        }

        var now = DateTime.UtcNow;
        var existing = categories.FirstOrDefault(current => current.Id == category.Id);
        if (existing is null)
        {
            var created = new LocalCustomerCategory
            {
                Id = category.Id == Guid.Empty ? Guid.NewGuid() : category.Id,
                Name = name,
                IsSystemDefault = false,
                IsDeleted = false,
                IsDirty = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _db.CustomerCategories.Add(created);
            await _db.SaveChangesAsync(ct);
            return LocalMutationResult.Ok(created.Id, "고객분류를 추가했습니다.");
        }

        existing.Name = name;
        existing.IsSystemDefault = false;
        existing.IsDeleted = false;
        existing.IsDirty = true;
        existing.UpdatedAtUtc = now;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(existing.Id, "고객분류를 수정했습니다.");
    }

    public async Task<LocalMutationResult> DeleteCustomerCategoryAsync(Guid categoryId, CancellationToken ct = default)
    {
        await EnsureCustomerCategoryIntegrityAsync(ct);

        var category = await _db.CustomerCategories.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Id == categoryId, ct);
        if (category is null)
            return LocalMutationResult.Missing("고객분류를 찾을 수 없습니다.");

        var inUse = await _db.Customers.IgnoreQueryFilters().AnyAsync(customer => customer.CategoryId == categoryId, ct) ||
                    await _db.CustomerMasters.IgnoreQueryFilters().AnyAsync(customer => customer.CategoryId == categoryId, ct);
        if (inUse)
            return LocalMutationResult.Denied("사용 중인 고객분류는 삭제할 수 없습니다.");

        category.IsDeleted = true;
        category.IsDirty = true;
        category.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(category.Id, "고객분류를 삭제했습니다.");
    }

    public async Task<LocalMutationResult> SavePriceGradeOptionAsync(LocalPriceGradeOption option, string? previousName = null, CancellationToken ct = default)
    {
        if (option is null)
            throw new ArgumentNullException(nameof(option));

        var name = (option.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
            return LocalMutationResult.Denied("가격등급 이름을 입력하세요.");

        var source = SelectionOptionDefaults.NormalizePriceSource(option.PriceSource);
        var options = await _db.PriceGradeOptions.IgnoreQueryFilters().ToListAsync(ct);
        if (options.Any(current =>
                current.Id != option.Id &&
                !current.IsDeleted &&
                string.Equals(current.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            return LocalMutationResult.Denied("같은 이름의 가격등급이 이미 있습니다.");
        }

        var now = DateTime.UtcNow;
        var existing = options.FirstOrDefault(current => current.Id == option.Id);
        var oldName = (previousName ?? existing?.Name ?? string.Empty).Trim();
        if (existing is null)
        {
            var created = new LocalPriceGradeOption
            {
                Id = option.Id == Guid.Empty ? Guid.NewGuid() : option.Id,
                Name = name,
                PriceSource = source,
                SortOrder = option.SortOrder,
                IsSystemDefault = false,
                IsActive = true,
                IsDeleted = false,
                IsDirty = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _db.PriceGradeOptions.Add(created);
            await _db.SaveChangesAsync(ct);
            return LocalMutationResult.Ok(created.Id, "가격등급을 추가했습니다.");
        }

        existing.Name = name;
        existing.PriceSource = source;
        existing.SortOrder = option.SortOrder;
        existing.IsSystemDefault = false;
        existing.IsActive = true;
        existing.IsDeleted = false;
        existing.IsDirty = true;
        existing.UpdatedAtUtc = now;

        if (!string.IsNullOrWhiteSpace(oldName) && !string.Equals(oldName, name, StringComparison.CurrentCulture))
        {
            var customers = await _db.Customers.IgnoreQueryFilters()
                .Where(customer => customer.PriceGrade == oldName)
                .ToListAsync(ct);
            foreach (var customer in customers)
            {
                customer.PriceGrade = name;
                customer.IsDirty = true;
                customer.UpdatedAtUtc = now;
            }
        }

        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(existing.Id, "가격등급을 수정했습니다.");
    }

    public async Task<LocalMutationResult> DeletePriceGradeOptionAsync(Guid optionId, CancellationToken ct = default)
    {
        var option = await _db.PriceGradeOptions.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Id == optionId, ct);
        if (option is null)
            return LocalMutationResult.Missing("가격등급을 찾을 수 없습니다.");

        var inUse = await _db.Customers.IgnoreQueryFilters().AnyAsync(customer => customer.PriceGrade == option.Name, ct);
        if (inUse)
            return LocalMutationResult.Denied("사용 중인 가격등급은 삭제할 수 없습니다.");

        option.IsDeleted = true;
        option.IsDirty = true;
        option.IsActive = false;
        option.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(option.Id, "가격등급을 삭제했습니다.");
    }

    public async Task<LocalMutationResult> SaveTradeTypeOptionAsync(LocalTradeTypeOption option, string? previousName = null, CancellationToken ct = default)
    {
        if (option is null)
            throw new ArgumentNullException(nameof(option));

        var name = CustomerTradeTypes.Normalize(option.Name);
        if (string.IsNullOrWhiteSpace(name))
            return LocalMutationResult.Denied("거래구분 이름을 입력하세요.");

        if (!option.AllowsSales && !option.AllowsPurchase)
            return LocalMutationResult.Denied("거래구분은 매출 또는 매입 중 하나 이상 허용되어야 합니다.");

        var options = await _db.TradeTypeOptions.IgnoreQueryFilters().ToListAsync(ct);
        if (options.Any(current =>
                current.Id != option.Id &&
                !current.IsDeleted &&
                string.Equals(current.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            return LocalMutationResult.Denied("같은 이름의 거래구분이 이미 있습니다.");
        }

        var now = DateTime.UtcNow;
        var existing = options.FirstOrDefault(current => current.Id == option.Id);
        var oldName = CustomerTradeTypes.Normalize(previousName ?? existing?.Name);
        if (existing is null)
        {
            var created = new LocalTradeTypeOption
            {
                Id = option.Id == Guid.Empty ? Guid.NewGuid() : option.Id,
                Name = name,
                AllowsSales = option.AllowsSales,
                AllowsPurchase = option.AllowsPurchase,
                SortOrder = option.SortOrder,
                IsSystemDefault = false,
                IsActive = true,
                IsDeleted = false,
                IsDirty = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _db.TradeTypeOptions.Add(created);
            await _db.SaveChangesAsync(ct);
            return LocalMutationResult.Ok(created.Id, "거래구분을 추가했습니다.");
        }

        existing.Name = name;
        existing.AllowsSales = option.AllowsSales;
        existing.AllowsPurchase = option.AllowsPurchase;
        existing.SortOrder = option.SortOrder;
        existing.IsSystemDefault = false;
        existing.IsActive = true;
        existing.IsDeleted = false;
        existing.IsDirty = true;
        existing.UpdatedAtUtc = now;

        if (!string.IsNullOrWhiteSpace(oldName) && !string.Equals(oldName, name, StringComparison.CurrentCulture))
        {
            var customers = await _db.Customers.IgnoreQueryFilters()
                .Where(customer => customer.TradeType == oldName)
                .ToListAsync(ct);
            foreach (var customer in customers)
            {
                customer.TradeType = name;
                customer.IsDirty = true;
                customer.UpdatedAtUtc = now;
            }
        }

        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(existing.Id, "거래구분을 수정했습니다.");
    }

    public async Task<LocalMutationResult> DeleteTradeTypeOptionAsync(Guid optionId, CancellationToken ct = default)
    {
        var option = await _db.TradeTypeOptions.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Id == optionId, ct);
        if (option is null)
            return LocalMutationResult.Missing("거래구분을 찾을 수 없습니다.");

        var inUse = await _db.Customers.IgnoreQueryFilters().AnyAsync(customer => customer.TradeType == option.Name, ct);
        if (inUse)
            return LocalMutationResult.Denied("사용 중인 거래구분은 삭제할 수 없습니다.");

        option.IsDeleted = true;
        option.IsDirty = true;
        option.IsActive = false;
        option.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return LocalMutationResult.Ok(option.Id, "거래구분을 삭제했습니다.");
    }

    public async Task<LocalMutationResult> SaveItemCategoryOptionAsync(LocalItemCategoryOption option, string? previousName = null, CancellationToken ct = default)
    {
        if (option is null)
            throw new ArgumentNullException(nameof(option));

        var name = SelectionOptionDefaults.NormalizeItemCategoryName(option.Name);
        if (string.IsNullOrWhiteSpace(name))
            return LocalMutationResult.Denied("품목분류 이름을 입력하세요.");

        var options = _db.ItemCategoryOptions.Local
            .Concat(await _db.ItemCategoryOptions.IgnoreQueryFilters().ToListAsync(ct))
            .GroupBy(option => option.Id)
            .Select(group => group.First())
            .ToList();
        if (options.Any(current =>
                current.Id != option.Id &&
                !current.IsDeleted &&
                string.Equals(
                    SelectionOptionDefaults.NormalizeItemCategoryName(current.Name),
                    name,
                    StringComparison.CurrentCultureIgnoreCase)))
        {
            return LocalMutationResult.Denied("같은 이름의 품목분류가 이미 있습니다.");
        }

        var now = DateTime.UtcNow;
        var existing = options.FirstOrDefault(current => current.Id == option.Id);
        var oldName = SelectionOptionDefaults.NormalizeItemCategoryName(previousName ?? existing?.Name);
        if (existing is null)
        {
            var created = new LocalItemCategoryOption
            {
                Id = option.Id == Guid.Empty ? Guid.NewGuid() : option.Id,
                Name = name,
                SortOrder = option.SortOrder,
                IsSystemDefault = false,
                IsActive = true,
                IsDeleted = false,
                IsDirty = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _db.ItemCategoryOptions.Add(created);
            await _db.SaveChangesAsync(ct);
            RaiseInventoryStateChanged();
            return LocalMutationResult.Ok(created.Id, "품목분류를 추가했습니다.");
        }

        existing.Name = name;
        existing.SortOrder = option.SortOrder;
        existing.IsSystemDefault = false;
        existing.IsActive = true;
        existing.IsDeleted = false;
        existing.IsDirty = true;
        existing.UpdatedAtUtc = now;

        if (!string.IsNullOrWhiteSpace(oldName) && !string.Equals(oldName, name, StringComparison.CurrentCulture))
        {
            var oldKey = RentalCatalogValueNormalizer.NormalizeLooseKey(oldName);
            var items = await _db.Items.IgnoreQueryFilters().ToListAsync(ct);
            foreach (var item in items.Where(item =>
                         string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(item.CategoryName), oldKey, StringComparison.OrdinalIgnoreCase)))
            {
                item.CategoryName = name;
                item.IsDirty = true;
                item.UpdatedAtUtc = now;
            }

            var rentalAssets = await _db.RentalAssets.IgnoreQueryFilters().ToListAsync(ct);
            foreach (var asset in rentalAssets.Where(asset =>
                         string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.ItemCategoryName), oldKey, StringComparison.OrdinalIgnoreCase)))
            {
                asset.ItemCategoryName = name;
                asset.IsDirty = true;
                asset.UpdatedAtUtc = now;
            }
        }

        await _db.SaveChangesAsync(ct);
        RaiseInventoryStateChanged();
        return LocalMutationResult.Ok(existing.Id, "품목분류를 수정했습니다.");
    }

    public async Task<LocalMutationResult> DeleteItemCategoryOptionAsync(Guid optionId, CancellationToken ct = default)
    {
        var option = await _db.ItemCategoryOptions.IgnoreQueryFilters().FirstOrDefaultAsync(current => current.Id == optionId, ct);
        if (option is null)
            return LocalMutationResult.Missing("품목분류를 찾을 수 없습니다.");

        var optionKey = RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name);
        var itemInUse = (await _db.Items.IgnoreQueryFilters().ToListAsync(ct)).Any(item =>
            string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(item.CategoryName), optionKey, StringComparison.OrdinalIgnoreCase));
        var rentalInUse = (await _db.RentalAssets.IgnoreQueryFilters().ToListAsync(ct)).Any(asset =>
            string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(asset.ItemCategoryName), optionKey, StringComparison.OrdinalIgnoreCase));
        if (itemInUse || rentalInUse)
            return LocalMutationResult.Denied("사용 중인 품목분류는 삭제할 수 없습니다.");

        option.IsDeleted = true;
        option.IsDirty = true;
        option.IsActive = false;
        option.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        RaiseInventoryStateChanged();
        return LocalMutationResult.Ok(option.Id, "품목분류를 삭제했습니다.");
    }

    // Settings
    public async Task<string?> GetSettingAsync(string key, CancellationToken ct = default)
    {
        var setting = await _db.Settings.FindAsync([key], ct);
        return setting?.Value;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        var setting = await _db.Settings.FindAsync([key], ct);
        if (setting is null)
            _db.Settings.Add(new LocalSetting { Key = key, Value = value });
        else
            setting.Value = value;

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

    public async Task<List<AttachmentSelectionState>> GetAttachmentSelectionsAsync(
        string customerKey,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customerKey))
            return new List<AttachmentSelectionState>();

        try
        {
            return await _db.AttachmentSelections
                .AsNoTracking()
                .Where(selection => selection.CustomerKey == customerKey)
                .OrderBy(selection => selection.OrderIndex ?? int.MaxValue)
                .ThenBy(selection => selection.DocCode)
                .Select(selection => new AttachmentSelectionState
                {
                    DocCode = selection.DocCode,
                    IsChecked = selection.IsChecked,
                    OrderIndex = selection.OrderIndex
                })
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            AppLogger.Error("AttachmentSelection", $"Failed to load attachment selections for key '{customerKey}'.", ex);
            return new List<AttachmentSelectionState>();
        }
    }

    public async Task SaveAttachmentSelectionsAsync(
        string customerKey,
        IReadOnlyCollection<AttachmentSelectionState> selections,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(customerKey))
            return;

        try
        {
            var incoming = selections ?? Array.Empty<AttachmentSelectionState>();
            var now = DateTime.UtcNow;
            var incomingByCode = incoming
                .Where(selection => !string.IsNullOrWhiteSpace(selection.DocCode))
                .GroupBy(selection => selection.DocCode, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

            var existing = await _db.AttachmentSelections
                .Where(selection => selection.CustomerKey == customerKey)
                .ToListAsync(ct);

            foreach (var row in existing)
            {
                if (!incomingByCode.TryGetValue(row.DocCode, out var state))
                {
                    _db.AttachmentSelections.Remove(row);
                    continue;
                }

                row.IsChecked = state.IsChecked;
                row.OrderIndex = state.OrderIndex;
                row.UpdatedAtUtc = now;
            }

            foreach (var state in incomingByCode.Values)
            {
                var exists = existing.Any(existingRow =>
                    string.Equals(existingRow.DocCode, state.DocCode, StringComparison.OrdinalIgnoreCase));
                if (exists)
                    continue;

                _db.AttachmentSelections.Add(new LocalAttachmentSelection
                {
                    CustomerKey = customerKey,
                    DocCode = state.DocCode,
                    IsChecked = state.IsChecked,
                    OrderIndex = state.OrderIndex,
                    UpdatedAtUtc = now
                });
            }

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            AppLogger.Error("AttachmentSelection", $"Failed to save attachment selections for key '{customerKey}'.", ex);
        }
    }

    // Session cache (offline fallback)
    public async Task SaveSessionCacheAsync(
        string username,
        string role,
        IEnumerable<string> permissions,
        string? tenantCode = null,
        string? scopeType = null,
        string? officeCode = null,
        CancellationToken ct = default)
    {
        await SetSettingAsync("CachedSession_Username", username, ct);
        await SetSettingAsync("CachedSession_Role", role, ct);
        await SetSettingAsync("CachedSession_Permissions", string.Join(',', permissions), ct);
        await SetSettingAsync("CachedSession_TenantCode", TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(tenantCode, officeCode), ct);
        await SetSettingAsync("CachedSession_ScopeType", TenantScopeCatalog.NormalizeScopeTypeOrDefault(scopeType, DomainConstants.IsAdminRole(role) ? TenantScopeCatalog.ScopeAdmin : TenantScopeCatalog.ScopeOfficeOnly), ct);
        await SetSettingAsync("CachedSession_OfficeCode", NormalizeOfficeCode(officeCode, DomainConstants.OfficeUsenet), ct);
    }

    public async Task<UserSessionDto?> GetCachedSessionAsync(string username, CancellationToken ct = default)
    {
        var cachedUsername = await GetSettingAsync("CachedSession_Username", ct);
        if (!string.Equals(cachedUsername, username, StringComparison.OrdinalIgnoreCase))
            return null;

        var role = await GetSettingAsync("CachedSession_Role", ct) ?? "User";
        var tenantCode = await GetSettingAsync("CachedSession_TenantCode", ct) ?? string.Empty;
        var scopeType = await GetSettingAsync("CachedSession_ScopeType", ct) ?? string.Empty;
        var officeCode = await GetSettingAsync("CachedSession_OfficeCode", ct) ?? string.Empty;
        var permissionsRaw = await GetSettingAsync("CachedSession_Permissions", ct) ?? string.Empty;
        var permissions = permissionsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

        return new UserSessionDto
        {
            UserId = Guid.Empty,
            Username = cachedUsername ?? username,
            Role = role,
            TenantCode = tenantCode,
            OfficeCode = officeCode,
            ScopeType = scopeType,
            Permissions = permissions
        };
    }

    public Task<string?> GetCachedOfficeCodeAsync(CancellationToken ct = default)
        => GetSettingAsync("CachedSession_OfficeCode", ct);

    // Transactions
    public Task<List<LocalTransaction>> GetTransactionsAsync(Guid customerId, CancellationToken ct = default)
        => _db.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.CustomerId == customerId)
            .OrderByDescending(transaction => transaction.TransactionDate)
            .ToListAsync(ct);

    public Task<List<LocalTransaction>> GetTransactionsAsync(
        Guid customerId,
        SessionState session,
        CancellationToken ct = default)
    {
        var query = _db.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.CustomerId == customerId);
        query = ApplyTransactionScope(query, session);
        return query
            .OrderByDescending(transaction => transaction.TransactionDate)
            .ToListAsync(ct);
    }

    public Task<List<LocalTransaction>> GetTransactionsAsync(
        DateOnly from,
        DateOnly to,
        Guid? customerId = null,
        CancellationToken ct = default)
    {
        var query = _db.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.TransactionDate >= from && transaction.TransactionDate <= to);

        if (customerId.HasValue)
            query = query.Where(transaction => transaction.CustomerId == customerId.Value);

        return query
            .OrderByDescending(transaction => transaction.TransactionDate)
            .ThenByDescending(transaction => transaction.CreatedAtUtc)
            .ToListAsync(ct);
    }

    public Task<List<LocalTransaction>> GetTransactionsAsync(
        DateOnly from,
        DateOnly to,
        Guid? customerId,
        SessionState session,
        CancellationToken ct = default)
    {
        var query = _db.Transactions
            .AsNoTracking()
            .Where(transaction => transaction.TransactionDate >= from && transaction.TransactionDate <= to);

        query = ApplyTransactionScope(query, session);

        if (customerId.HasValue)
            query = query.Where(transaction => transaction.CustomerId == customerId.Value);

        return query
            .OrderByDescending(transaction => transaction.TransactionDate)
            .ThenByDescending(transaction => transaction.CreatedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<decimal> GetAdvanceBalanceAsync(
        Guid customerId,
        SessionState session,
        CancellationToken ct = default)
    {
        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == customerId, ct);
        if (customer is null)
            return 0m;

        var customerOfficeCode = NormalizeOfficeScope(customer.ResponsibleOfficeCode, DomainConstants.OfficeUsenet);
        if (!CanAccessCustomer(customer.Id, customerOfficeCode, session, session.User?.Role))
            return 0m;

        return await GetAdvanceBalanceCoreAsync(customerId, ct);
    }

    public async Task<CustomerFinancialSummary> GetCustomerFinancialSummaryAsync(
        Guid customerId,
        SessionState session,
        CancellationToken ct = default)
    {
        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == customerId, ct);
        if (customer is null)
            return new CustomerFinancialSummary();

        var customerOfficeCode = NormalizeOfficeScope(customer.ResponsibleOfficeCode, DomainConstants.OfficeUsenet);
        if (!CanAccessCustomer(customer.Id, customerOfficeCode, session, session.User?.Role))
            return new CustomerFinancialSummary();

        var invoices = await _db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(invoice => invoice.Payments.Where(payment => !payment.IsDeleted))
            .Where(invoice => !invoice.IsDeleted && invoice.CustomerId == customerId && invoice.VoucherType == VoucherType.Sales)
            .ToListAsync(ct);

        var receivableAmount = invoices
            .Where(invoice => CanAccessInvoice(invoice, session))
            .Sum(invoice => Math.Max(0m, invoice.TotalAmount - invoice.Payments.Where(payment => !payment.IsDeleted).Sum(payment => payment.Amount)));

        var transactions = await _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction => !transaction.IsDeleted && transaction.CustomerId == customerId)
            .ToListAsync(ct);

        var prepaymentAmount = transactions
            .Where(transaction => transaction.AdvanceDelta > 0m &&
                                  (transaction.LinkedInvoiceId.HasValue || transaction.LinkedRentalBillingProfileId.HasValue))
            .Sum(transaction => transaction.AdvanceDelta);

        return new CustomerFinancialSummary
        {
            AdvanceBalance = transactions.Sum(transaction => transaction.AdvanceDelta),
            ReceivableAmount = receivableAmount,
            PrepaymentAmount = prepaymentAmount
        };
    }

    public async Task<InvoiceSettlementSummary> GetInvoiceSettlementSummaryAsync(
        Guid invoiceId,
        SessionState session,
        CancellationToken ct = default)
    {
        var invoice = await _db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == invoiceId, ct);
        if (invoice is null || !CanAccessInvoice(invoice, session))
            return new InvoiceSettlementSummary();

        var settledAmount = await GetInvoiceSettledAmountCoreAsync(invoiceId, ct);
        return new InvoiceSettlementSummary
        {
            InvoiceTotal = invoice.TotalAmount,
            SettledAmount = settledAmount,
            RemainingAmount = Math.Max(0m, invoice.TotalAmount - settledAmount)
        };
    }

    public async Task<RentalSettlementSummary> GetRentalSettlementSummaryAsync(
        Guid billingProfileId,
        SessionState session,
        CancellationToken ct = default)
    {
        var profile = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null || !CanAccessRentalProfile(profile, session))
            return new RentalSettlementSummary();

        var settledAmount = await GetRentalSettledAmountCoreAsync(billingProfileId, ct);
        var billedAmount = profile.MonthlyAmount;
        var outstandingAmount = Math.Max(0m, billedAmount - settledAmount);
        return new RentalSettlementSummary
        {
            BilledAmount = billedAmount,
            SettledAmount = settledAmount,
            OutstandingAmount = outstandingAmount,
            BillingStatus = outstandingAmount <= 0m
                ? PaymentFlowConstants.BillingStatusCompleted
                : string.IsNullOrWhiteSpace(profile.BillingStatus)
                    ? PaymentFlowConstants.BillingStatusInProgress
                    : profile.BillingStatus,
            SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount),
            CompletionStatus = outstandingAmount <= 0m
                ? PaymentFlowConstants.CompletionDone
                : PaymentFlowConstants.CompletionPending
        };
    }

    public async Task<LocalTransaction> SaveTransactionAsync(LocalTransaction transaction, CancellationToken ct = default)
    {
        transaction.IsDirty = true;
        transaction.UpdatedAtUtc = DateTime.UtcNow;

        var existing = await _db.Transactions.FindAsync([transaction.Id], ct);
        if (existing is null)
            _db.Transactions.Add(transaction);
        else
            _db.Entry(existing).CurrentValues.SetValues(transaction);

        await _db.SaveChangesAsync(ct);
        return transaction;
    }

    public async Task<OfficeMutationResult> SaveTransactionAsync(
        LocalTransaction transaction,
        SessionState session,
        CancellationToken ct = default)
    {
        if (transaction is null)
            throw new ArgumentNullException(nameof(transaction));

        if (!CanModifySharedBusinessData(session))
            return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 수금/지급을 저장할 수 있습니다.");

        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == transaction.CustomerId, ct);
        if (customer is null)
            return OfficeMutationResult.Missing("거래처를 찾을 수 없습니다.");

        var customerOfficeCode = NormalizeOfficeScope(customer.ResponsibleOfficeCode, DomainConstants.OfficeUsenet);
        if (!CanAccessCustomer(customer.Id, customerOfficeCode, session, session.User?.Role))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래처의 수금/지급을 저장할 수 없습니다.");

        var existing = await _db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transaction.Id, ct);
        if (existing is not null && !CanAccessTransaction(existing, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래처의 수금/지급을 저장할 수 없습니다.");

        var previousLinkedInvoiceId = existing?.LinkedInvoiceId;
        var previousLinkedRentalId = existing?.LinkedRentalBillingProfileId;

        transaction.TransactionKind = PaymentFlowConstants.NormalizeTransactionKind(
            transaction.TransactionKind,
            preferPayment: transaction.PaymentTotal > 0m && transaction.ReceiptTotal <= 0m);

        LocalInvoice? linkedInvoice = null;
        if (transaction.LinkedInvoiceId.HasValue && transaction.LinkedInvoiceId.Value != Guid.Empty)
        {
            linkedInvoice = await _db.Invoices
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == transaction.LinkedInvoiceId.Value, ct);
            if (linkedInvoice is null)
                return OfficeMutationResult.Missing("연결할 전표를 찾을 수 없습니다.");
            if (!CanAccessInvoice(linkedInvoice, session))
                return OfficeMutationResult.Denied("권한이 없어 해당 전표 결제를 처리할 수 없습니다.");

            transaction.LinkedInvoiceNumber = string.IsNullOrWhiteSpace(linkedInvoice.InvoiceNumber)
                ? linkedInvoice.LocalTempNumber
                : linkedInvoice.InvoiceNumber;

            var preferInvoicePayment = linkedInvoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement;
            if (!PaymentFlowConstants.IsInvoiceSettlementKind(transaction.TransactionKind))
            {
                transaction.TransactionKind = preferInvoicePayment
                    ? PaymentFlowConstants.TransactionKindInvoicePayment
                    : PaymentFlowConstants.TransactionKindInvoiceReceipt;
            }
        }
        else
        {
            transaction.LinkedInvoiceId = null;
            transaction.LinkedInvoiceNumber = string.Empty;
        }

        LocalRentalBillingProfile? linkedRentalProfile = null;
        if (transaction.LinkedRentalBillingProfileId.HasValue && transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
        {
            linkedRentalProfile = await _db.RentalBillingProfiles
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(current => current.Id == transaction.LinkedRentalBillingProfileId.Value, ct);
            if (linkedRentalProfile is null)
                return OfficeMutationResult.Missing("연결할 렌탈 청구 대상을 찾을 수 없습니다.");
            if (!CanAccessRentalProfile(linkedRentalProfile, session))
                return OfficeMutationResult.Denied("권한이 없어 해당 렌탈 청구 결제를 처리할 수 없습니다.");

            if (!PaymentFlowConstants.IsRentalSettlementKind(transaction.TransactionKind))
                transaction.TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt;
        }
        else
        {
            transaction.LinkedRentalBillingProfileId = null;
        }

        var derivedResponsibleOfficeCode = string.IsNullOrWhiteSpace(transaction.ResponsibleOfficeCode)
            ? linkedInvoice?.ResponsibleOfficeCode
                ?? linkedRentalProfile?.ResponsibleOfficeCode
                ?? linkedRentalProfile?.ManagementCompanyCode
                ?? existing?.ResponsibleOfficeCode
                ?? customerOfficeCode
            : transaction.ResponsibleOfficeCode;

        transaction.ResponsibleOfficeCode = NormalizeOfficeScope(derivedResponsibleOfficeCode, customerOfficeCode);

        var receiptTotal = Math.Max(0m, transaction.ReceiptTotal);
        var paymentTotal = Math.Max(0m, transaction.PaymentTotal);
        var absoluteAmount = receiptTotal > 0m ? receiptTotal : paymentTotal;
        transaction.SettlementAmount = Math.Max(0m, transaction.SettlementAmount);
        transaction.AdvanceDelta = 0m;

        switch (transaction.TransactionKind)
        {
            case var kind when kind == PaymentFlowConstants.TransactionKindAdvanceDeposit:
                transaction.LinkedInvoiceId = null;
                transaction.LinkedInvoiceNumber = string.Empty;
                transaction.LinkedRentalBillingProfileId = null;
                transaction.SettlementAmount = 0m;
                transaction.AdvanceDelta = receiptTotal;
                break;

            case var kind when kind == PaymentFlowConstants.TransactionKindAdvanceRefund:
                transaction.LinkedInvoiceId = null;
                transaction.LinkedInvoiceNumber = string.Empty;
                transaction.LinkedRentalBillingProfileId = null;
                transaction.SettlementAmount = 0m;
                transaction.AdvanceDelta = -paymentTotal;
                break;

            case var kind when kind == PaymentFlowConstants.TransactionKindAdvanceApply:
                if (linkedInvoice is null)
                    return OfficeMutationResult.Denied("선수금 차감은 연결 전표가 있어야 합니다.");
                transaction.SettlementAmount = transaction.SettlementAmount > 0m
                    ? transaction.SettlementAmount
                    : absoluteAmount;
                if (transaction.SettlementAmount <= 0m)
                    return OfficeMutationResult.Denied("선수금 차감 금액을 입력하세요.");
                transaction.AdvanceDelta = -transaction.SettlementAmount;
                break;

            case var kind when kind == PaymentFlowConstants.TransactionKindInvoiceReceipt
                                || kind == PaymentFlowConstants.TransactionKindInvoicePayment:
                if (linkedInvoice is null)
                    return OfficeMutationResult.Denied("전표 수금/지급은 연결 전표가 있어야 합니다.");
                transaction.SettlementAmount = transaction.SettlementAmount > 0m
                    ? Math.Min(transaction.SettlementAmount, absoluteAmount <= 0m ? transaction.SettlementAmount : absoluteAmount)
                    : absoluteAmount;
                if (transaction.SettlementAmount <= 0m)
                    return OfficeMutationResult.Denied("전표 결제 금액을 입력하세요.");

                if (kind == PaymentFlowConstants.TransactionKindInvoiceReceipt && receiptTotal > transaction.SettlementAmount)
                    transaction.AdvanceDelta = receiptTotal - transaction.SettlementAmount;
                break;

            case var kind when kind == PaymentFlowConstants.TransactionKindRentalReceipt:
                if (linkedRentalProfile is null)
                    return OfficeMutationResult.Denied("렌탈 수금은 연결 청구건이 있어야 합니다.");
                transaction.SettlementAmount = transaction.SettlementAmount > 0m
                    ? Math.Min(transaction.SettlementAmount, receiptTotal <= 0m ? transaction.SettlementAmount : receiptTotal)
                    : receiptTotal;
                if (transaction.SettlementAmount <= 0m)
                    return OfficeMutationResult.Denied("렌탈 수금 금액을 입력하세요.");
                break;

            default:
                transaction.LinkedInvoiceId = null;
                transaction.LinkedInvoiceNumber = string.Empty;
                transaction.LinkedRentalBillingProfileId = null;
                transaction.SettlementAmount = 0m;
                break;
        }

        transaction.IsDirty = true;
        transaction.UpdatedAtUtc = DateTime.UtcNow;

        if (existing is null)
            _db.Transactions.Add(transaction);
        else
            _db.Entry(existing).CurrentValues.SetValues(transaction);

        await _db.SaveChangesAsync(ct);

        if (linkedInvoice is not null)
            await SyncInvoicePaymentFromTransactionAsync(transaction, linkedInvoice, ct);
        else
            await RemoveLinkedInvoicePaymentAsync(transaction.Id, ct);

        if (linkedRentalProfile is not null)
            await RecalculateRentalSettlementAsync(linkedRentalProfile.Id, ct);

        if (previousLinkedRentalId.HasValue &&
            previousLinkedRentalId != transaction.LinkedRentalBillingProfileId &&
            previousLinkedRentalId.Value != Guid.Empty)
        {
            await RecalculateRentalSettlementAsync(previousLinkedRentalId.Value, ct);
        }

        return OfficeMutationResult.Ok(transaction.Id, "수금/지급을 저장했습니다.");
    }

    private async Task<decimal> GetAdvanceBalanceCoreAsync(Guid customerId, CancellationToken ct)
    {
        var values = await _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction => !transaction.IsDeleted && transaction.CustomerId == customerId)
            .Select(transaction => transaction.AdvanceDelta)
            .ToListAsync(ct);

        return values.Sum();
    }

    private async Task<decimal> GetInvoiceSettledAmountCoreAsync(Guid invoiceId, CancellationToken ct)
    {
        var values = await _db.Payments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(payment => !payment.IsDeleted && payment.InvoiceId == invoiceId)
            .Select(payment => payment.Amount)
            .ToListAsync(ct);

        return values.Sum();
    }

    private async Task<decimal> GetRentalSettledAmountCoreAsync(Guid billingProfileId, CancellationToken ct)
    {
        var values = await _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedRentalBillingProfileId == billingProfileId)
            .Select(transaction => transaction.SettlementAmount)
            .ToListAsync(ct);

        return values.Sum();
    }

    private async Task SyncInvoicePaymentFromTransactionAsync(
        LocalTransaction transaction,
        LocalInvoice invoice,
        CancellationToken ct)
    {
        var amount = Math.Max(0m, transaction.SettlementAmount);
        var payment = await _db.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transaction.Id, ct);

        if (amount <= 0m)
        {
            if (payment is not null)
            {
                payment.IsDeleted = true;
                payment.IsDirty = true;
                payment.UpdatedAtUtc = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }

            return;
        }

        var transactionKindLabel = PaymentFlowConstants.GetTransactionKindDisplayName(transaction.TransactionKind);
        var note = string.IsNullOrWhiteSpace(transaction.Note)
            ? transactionKindLabel
            : $"{transactionKindLabel} - {transaction.Note.Trim()}";

        if (payment is null)
        {
            _db.Payments.Add(new LocalPayment
            {
                Id = transaction.Id,
                InvoiceId = invoice.Id,
                PaymentDate = transaction.TransactionDate,
                Amount = amount,
                Note = note,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                IsDirty = true
            });
        }
        else
        {
            payment.InvoiceId = invoice.Id;
            payment.PaymentDate = transaction.TransactionDate;
            payment.Amount = amount;
            payment.Note = note;
            payment.IsDeleted = false;
            payment.IsDirty = true;
            payment.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }

    private async Task RemoveLinkedInvoicePaymentAsync(Guid transactionId, CancellationToken ct)
    {
        var payment = await _db.Payments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transactionId, ct);
        if (payment is null)
            return;

        payment.IsDeleted = true;
        payment.IsDirty = true;
        payment.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private async Task RecalculateRentalSettlementAsync(Guid billingProfileId, CancellationToken ct)
    {
        var profile = await _db.RentalBillingProfiles
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == billingProfileId, ct);
        if (profile is null)
            return;

        var settledAmount = await GetRentalSettledAmountCoreAsync(billingProfileId, ct);
        var billedAmount = Math.Max(0m, profile.MonthlyAmount);
        profile.SettledAmount = settledAmount;
        profile.OutstandingAmount = Math.Max(0m, billedAmount - settledAmount);
        profile.SettlementStatus = DetermineRentalSettlementStatus(profile.BillingMethod, settledAmount, billedAmount);
        profile.CompletionStatus = profile.OutstandingAmount <= 0m
            ? PaymentFlowConstants.CompletionDone
            : PaymentFlowConstants.CompletionPending;

        if (profile.CompletionStatus == PaymentFlowConstants.CompletionDone)
        {
            profile.BillingStatus = PaymentFlowConstants.BillingStatusCompleted;
            profile.LastSettledDate = await _db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(transaction => !transaction.IsDeleted && transaction.LinkedRentalBillingProfileId == billingProfileId)
                .OrderByDescending(transaction => transaction.TransactionDate)
                .Select(transaction => (DateOnly?)transaction.TransactionDate)
                .FirstOrDefaultAsync(ct);
        }
        else if (!string.Equals(profile.BillingStatus, PaymentFlowConstants.BillingStatusOnHold, StringComparison.OrdinalIgnoreCase) &&
                 !string.Equals(profile.BillingStatus, PaymentFlowConstants.BillingStatusCancelled, StringComparison.OrdinalIgnoreCase))
        {
            profile.BillingStatus = PaymentFlowConstants.BillingStatusInProgress;
            profile.LastSettledDate = settledAmount > 0m
                ? await _db.Transactions
                    .IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(transaction => !transaction.IsDeleted && transaction.LinkedRentalBillingProfileId == billingProfileId)
                    .OrderByDescending(transaction => transaction.TransactionDate)
                    .Select(transaction => (DateOnly?)transaction.TransactionDate)
                    .FirstOrDefaultAsync(ct)
                : null;
        }

        profile.IsDirty = true;
        profile.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static string DetermineRentalSettlementStatus(string? billingMethod, decimal settledAmount, decimal billedAmount)
    {
        if (settledAmount <= 0m)
            return PaymentFlowConstants.GetPendingSettlementStatus(billingMethod, billingMethod);
        if (settledAmount < billedAmount)
            return PaymentFlowConstants.SettlementStatusPartial;
        return PaymentFlowConstants.GetDisplaySettlementCompleteStatus(billingMethod, billingMethod);
    }

    private bool CanAccessRentalProfile(LocalRentalBillingProfile profile, SessionState session)
        => session.HasGlobalDataScope ||
           session.HasAssignedPermission(AppPermissionNames.RentalViewAll) ||
           session.HasAssignedPermission(AppPermissionNames.RentalEditAll) ||
           string.Equals(
               NormalizeOfficeCode(profile.ResponsibleOfficeCode, DomainConstants.OfficeUsenet),
               NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet),
               StringComparison.OrdinalIgnoreCase) ||
           string.Equals(profile.AssignedUsername, session.User?.Username ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    public async Task<List<LocalTransactionAttachment>> GetTransactionAttachmentsAsync(
        Guid transactionId,
        SessionState session,
        CancellationToken ct = default)
    {
        var transaction = await _db.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == transactionId, ct);
        if (transaction is null || !CanAccessTransaction(transaction, session))
            return new List<LocalTransactionAttachment>();

        return await _db.TransactionAttachments
            .AsNoTracking()
            .Where(current => current.TransactionId == transactionId)
            .OrderBy(current => current.SortOrder)
            .ThenByDescending(current => current.UploadedAtUtc)
            .ToListAsync(ct);
    }

    public async Task<OfficeMutationResult> SaveTransactionAttachmentAsync(
        Guid transactionId,
        string sourceFilePath,
        string attachmentType,
        string? description,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanModifySharedBusinessData(session))
            return OfficeMutationResult.Denied("관리자 또는 god 권한 계정만 증빙 파일을 등록할 수 있습니다.");

        if (transactionId == Guid.Empty)
            return OfficeMutationResult.Denied("증빙을 연결할 수금/지급 내역을 먼저 선택하세요.");

        if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            return OfficeMutationResult.Missing("첨부할 파일을 찾을 수 없습니다.");

        var transaction = await _db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transactionId, ct);
        if (transaction is null)
            return OfficeMutationResult.Missing("수금/지급 내역을 찾을 수 없습니다.");

        if (!CanAccessTransaction(transaction, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래의 증빙을 저장할 수 없습니다.");

        var now = DateTime.UtcNow;
        var originalFileName = Path.GetFileName(sourceFilePath);
        var fileExtension = Path.GetExtension(sourceFilePath);
        var attachmentDir = Path.Combine(AppPaths.TransactionAttachmentsDir, transactionId.ToString("N"));
        Directory.CreateDirectory(attachmentDir);

        var storedFileName = $"{now:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{fileExtension}";
        var storedPath = Path.Combine(attachmentDir, storedFileName);
        File.Copy(sourceFilePath, storedPath, true);

        var sortOrder = await _db.TransactionAttachments
            .IgnoreQueryFilters()
            .Where(current => current.TransactionId == transactionId)
            .CountAsync(ct);

        var attachment = new LocalTransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            AttachmentType = NormalizeAttachmentType(attachmentType),
            FileName = originalFileName,
            StoredFileName = storedFileName,
            StoredPath = storedPath,
            MimeType = ResolveMimeType(fileExtension),
            FileSize = new FileInfo(storedPath).Length,
            FileHash = ComputeFileHash(storedPath),
            Description = (description ?? string.Empty).Trim(),
            UploadedByUsername = session.User?.Username ?? "local-user",
            UploadedAtUtc = now,
            VerificationStatus = "미확인",
            SortOrder = sortOrder,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            IsDirty = true
        };

        _db.TransactionAttachments.Add(attachment);
        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalTransactionAttachment),
            EntityId = attachment.Id.ToString("D"),
            Action = "Create",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = string.Empty,
            AfterJson = JsonSerializer.Serialize(new
            {
                attachment.TransactionId,
                attachment.AttachmentType,
                attachment.FileName,
                attachment.VerificationStatus
            }, AuditJsonOptions),
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(attachment.Id, "수금/지급 증빙을 첨부했습니다.");
    }

    public async Task<OfficeMutationResult> DeleteTransactionAttachmentAsync(
        Guid attachmentId,
        SessionState session,
        CancellationToken ct = default)
    {
        var attachment = await _db.TransactionAttachments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == attachmentId, ct);
        if (attachment is null)
            return OfficeMutationResult.Missing("삭제할 증빙을 찾을 수 없습니다.");

        var transaction = await _db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == attachment.TransactionId, ct);
        if (transaction is null)
            return OfficeMutationResult.Missing("증빙과 연결된 수금/지급 내역을 찾을 수 없습니다.");

        if (!CanAccessTransaction(transaction, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 증빙을 삭제할 수 없습니다.");

        if (string.Equals(attachment.VerificationStatus, "확인완료", StringComparison.OrdinalIgnoreCase) && !session.HasAdministrativePrivileges)
            return OfficeMutationResult.Denied("확인완료된 증빙은 관리자만 삭제할 수 있습니다.");

        attachment.IsDeleted = true;
        attachment.IsDirty = true;
        attachment.UpdatedAtUtc = DateTime.UtcNow;

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalTransactionAttachment),
            EntityId = attachment.Id.ToString("D"),
            Action = "Delete",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = JsonSerializer.Serialize(new
            {
                attachment.TransactionId,
                attachment.AttachmentType,
                attachment.FileName,
                attachment.VerificationStatus
            }, AuditJsonOptions),
            AfterJson = string.Empty,
            CreatedAtUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(attachment.Id, "수금/지급 증빙을 삭제했습니다.");
    }

    public async Task<OfficeMutationResult> UpdateTransactionAttachmentVerificationAsync(
        Guid attachmentId,
        string verificationStatus,
        string? verificationMemo,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!session.HasAdministrativePrivileges)
            return OfficeMutationResult.Denied("증빙 확인 상태는 관리자만 변경할 수 있습니다.");

        var attachment = await _db.TransactionAttachments
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == attachmentId, ct);
        if (attachment is null)
            return OfficeMutationResult.Missing("증빙을 찾을 수 없습니다.");

        var transaction = await _db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == attachment.TransactionId, ct);
        if (transaction is null)
            return OfficeMutationResult.Missing("증빙과 연결된 수금/지급 내역을 찾을 수 없습니다.");

        var normalizedStatus = NormalizeAttachmentVerificationStatus(verificationStatus);
        var beforeJson = JsonSerializer.Serialize(new
        {
            attachment.VerificationStatus,
            attachment.VerifiedByUsername,
            attachment.VerifiedAtUtc,
            attachment.VerificationMemo
        }, AuditJsonOptions);

        attachment.VerificationStatus = normalizedStatus;
        attachment.VerificationMemo = (verificationMemo ?? string.Empty).Trim();
        attachment.VerifiedByUsername = session.User?.Username ?? "local-user";
        attachment.VerifiedAtUtc = DateTime.UtcNow;
        attachment.IsDirty = true;
        attachment.UpdatedAtUtc = attachment.VerifiedAtUtc.Value;

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalTransactionAttachment),
            EntityId = attachment.Id.ToString("D"),
            Action = normalizedStatus,
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleAdmin,
            OfficeCode = session.OfficeCode,
            BeforeJson = beforeJson,
            AfterJson = JsonSerializer.Serialize(new
            {
                attachment.VerificationStatus,
                attachment.VerifiedByUsername,
                attachment.VerifiedAtUtc,
                attachment.VerificationMemo
            }, AuditJsonOptions),
            CreatedAtUtc = attachment.VerifiedAtUtc.Value
        });

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(attachment.Id, $"증빙 상태를 {normalizedStatus}(으)로 변경했습니다.");
    }

    // Inventory transfers
    public Task<List<LocalInventoryTransfer>> GetInventoryTransfersAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken ct = default)
    {
        var query = _db.InventoryTransfers
            .Include(transfer => transfer.Lines.Where(line => !line.IsDeleted))
            .AsNoTracking();

        if (from.HasValue)
            query = query.Where(transfer => transfer.TransferDate >= from.Value);

        if (to.HasValue)
            query = query.Where(transfer => transfer.TransferDate <= to.Value);

        return query
            .OrderByDescending(transfer => transfer.TransferDate)
            .ThenByDescending(transfer => transfer.UpdatedAtUtc)
            .ToListAsync(ct);
    }

    public Task<LocalInventoryTransfer?> GetInventoryTransferAsync(Guid transferId, CancellationToken ct = default)
        => _db.InventoryTransfers
            .Include(transfer => transfer.Lines.Where(line => !line.IsDeleted))
            .AsNoTracking()
            .FirstOrDefaultAsync(transfer => transfer.Id == transferId, ct);

    public async Task<OfficeMutationResult> SaveInventoryTransferAsync(
        LocalInventoryTransfer transfer,
        SessionState session,
        CancellationToken ct = default)
    {
        if (transfer is null)
            throw new ArgumentNullException(nameof(transfer));

        var now = DateTime.UtcNow;
        var fromWarehouseCode = NormalizeWarehouseCode(
            transfer.FromWarehouseCode,
            session.OfficeCode,
            DomainConstants.OfficeUsenet);
        var toWarehouseCode = NormalizeWarehouseCode(
            transfer.ToWarehouseCode,
            session.OfficeCode,
            DomainConstants.OfficeYeonsu);

        if (string.Equals(fromWarehouseCode, toWarehouseCode, StringComparison.OrdinalIgnoreCase))
            return OfficeMutationResult.Denied("출발창고와 도착창고는 서로 달라야 합니다.");

        var validLines = (transfer.Lines ?? new List<LocalInventoryTransferLine>())
            .Where(line => !line.IsDeleted &&
                           line.ItemId.HasValue &&
                           !string.IsNullOrWhiteSpace(line.ItemNameOriginal) &&
                           line.Quantity > 0m)
            .ToList();

        if (validLines.Count == 0)
            return OfficeMutationResult.Denied("이동 품목을 1개 이상 입력하세요.");

        var existing = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(current => current.Lines)
            .FirstOrDefaultAsync(current => current.Id == transfer.Id, ct);

        if (existing is not null && IsFinalTransferStatus(existing.TransferStatus))
            return OfficeMutationResult.Denied("이미 수령확정 또는 반려된 재고이동 문서는 수정할 수 없습니다.");

        var transferId = existing?.Id ?? (transfer.Id == Guid.Empty ? Guid.NewGuid() : transfer.Id);
        var transferNumber = string.IsNullOrWhiteSpace(transfer.TransferNumber)
            ? await GenerateTransferNumberAsync(transfer.TransferDate, ct)
            : transfer.TransferNumber.Trim();

        var entity = existing ?? new LocalInventoryTransfer
        {
            Id = transferId,
            CreatedAtUtc = now
        };

        entity.TransferNumber = transferNumber;
        entity.TransferDate = transfer.TransferDate;
        entity.FromWarehouseCode = fromWarehouseCode;
        entity.ToWarehouseCode = toWarehouseCode;
        entity.Memo = transfer.Memo ?? string.Empty;
        entity.CreatedByUsername = string.IsNullOrWhiteSpace(existing?.CreatedByUsername)
            ? (session.User?.Username ?? "local-user")
            : existing.CreatedByUsername;
        entity.LastSavedByUsername = session.User?.Username ?? "local-user";
        entity.LastSavedAtUtc = now;
        entity.IsDeleted = false;
        entity.IsDirty = true;
        entity.UpdatedAtUtc = now;
        entity.TransferStatus = existing?.TransferStatus?.Trim() switch
        {
            null or "" => "수령대기",
            var value => value
        };
        entity.RequestedByUsername = string.IsNullOrWhiteSpace(existing?.RequestedByUsername)
            ? (session.User?.Username ?? "local-user")
            : existing!.RequestedByUsername;
        entity.RequestedAtUtc = existing?.RequestedAtUtc ?? now;
        entity.LastStatusChangedByUsername = string.IsNullOrWhiteSpace(existing?.LastStatusChangedByUsername)
            ? (session.User?.Username ?? "local-user")
            : existing!.LastStatusChangedByUsername;
        entity.LastStatusChangedAtUtc = existing?.LastStatusChangedAtUtc ?? now;
        entity.ReceivedByUsername = existing?.ReceivedByUsername ?? string.Empty;
        entity.ReceivedAtUtc = existing?.ReceivedAtUtc;
        entity.ReceiveMemo = existing?.ReceiveMemo ?? string.Empty;
        entity.ReceiveEvidencePath = existing?.ReceiveEvidencePath ?? string.Empty;
        entity.RejectedByUsername = existing?.RejectedByUsername ?? string.Empty;
        entity.RejectedAtUtc = existing?.RejectedAtUtc;
        entity.RejectReason = existing?.RejectReason ?? string.Empty;

        if (existing is null)
        {
            entity.Lines = CloneTransferLines(validLines, transferId);
            _db.InventoryTransfers.Add(entity);
        }
        else
        {
            _db.Entry(existing).CurrentValues.SetValues(entity);
            _db.InventoryTransferLines.RemoveRange(existing.Lines);
            existing.Lines.Clear();
            foreach (var line in CloneTransferLines(validLines, transferId))
                existing.Lines.Add(line);
        }

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalInventoryTransfer),
            EntityId = transferId.ToString("D"),
            Action = existing is null ? "Create" : "Update",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = string.Empty,
            AfterJson = string.Empty,
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);

        await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
        {
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            ForceOverride = false
        }, ct);

        return OfficeMutationResult.Ok(
            transferId,
            existing is null ? "재고이동을 저장했습니다." : "재고이동을 수정했습니다.");
    }

    public async Task<OfficeMutationResult> ConfirmInventoryTransferReceiptAsync(
        Guid transferId,
        IEnumerable<LocalInventoryTransferLine> receivedLines,
        string? receiveMemo,
        SessionState session,
        CancellationToken ct = default)
    {
        var transfer = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .Include(current => current.Lines)
            .FirstOrDefaultAsync(current => current.Id == transferId, ct);
        if (transfer is null)
            return OfficeMutationResult.Missing("재고이동 문서를 찾을 수 없습니다.");

        if (!CanReceiveInventoryTransfer(transfer, session))
            return OfficeMutationResult.Denied("도착지 담당자 또는 관리자만 수령확정할 수 있습니다.");

        if (string.Equals(transfer.TransferStatus, "수령확정", StringComparison.OrdinalIgnoreCase))
            return OfficeMutationResult.Denied("이미 수령확정된 문서입니다.");

        if (string.Equals(transfer.TransferStatus, "반려", StringComparison.OrdinalIgnoreCase))
            return OfficeMutationResult.Denied("반려된 문서는 수령확정할 수 없습니다.");

        var now = DateTime.UtcNow;
        var receivedLineMap = (receivedLines ?? Array.Empty<LocalInventoryTransferLine>())
            .ToDictionary(line => line.Id, line => line);

        foreach (var line in transfer.Lines.Where(current => !current.IsDeleted))
        {
            if (!receivedLineMap.TryGetValue(line.Id, out var received))
            {
                line.ReceivedQuantity = line.Quantity;
                line.QuantityDifference = 0m;
                line.ReceiptRemark = string.Empty;
                continue;
            }

            var receivedQuantity = received.ReceivedQuantity ?? line.Quantity;
            if (receivedQuantity < 0m)
                receivedQuantity = 0m;

            line.ReceivedQuantity = receivedQuantity;
            line.QuantityDifference = receivedQuantity - line.Quantity;
            line.ReceiptRemark = (received.ReceiptRemark ?? string.Empty).Trim();
        }

        transfer.TransferStatus = "수령확정";
        transfer.ReceiveMemo = (receiveMemo ?? string.Empty).Trim();
        transfer.ReceivedByUsername = session.User?.Username ?? "local-user";
        transfer.ReceivedAtUtc = now;
        transfer.LastStatusChangedByUsername = transfer.ReceivedByUsername;
        transfer.LastStatusChangedAtUtc = now;
        transfer.LastSavedByUsername = session.User?.Username ?? "local-user";
        transfer.LastSavedAtUtc = now;
        transfer.IsDirty = true;
        transfer.UpdatedAtUtc = now;

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalInventoryTransfer),
            EntityId = transferId.ToString("D"),
            Action = "ConfirmReceipt",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = string.Empty,
            AfterJson = JsonSerializer.Serialize(new
            {
                transfer.TransferStatus,
                transfer.ReceiveMemo,
                transfer.ReceivedByUsername,
                transfer.ReceivedAtUtc,
                Lines = transfer.Lines
                    .Where(line => !line.IsDeleted)
                    .Select(line => new
                    {
                        line.Id,
                        line.Quantity,
                        line.ReceivedQuantity,
                        line.QuantityDifference,
                        line.ReceiptRemark
                    })
            }, AuditJsonOptions),
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);
        await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
        {
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            ForceOverride = false
        }, ct);
        return OfficeMutationResult.Ok(transferId, "재고이동 수령을 확정했습니다.");
    }

    public async Task<OfficeMutationResult> DeleteInventoryTransferAsync(
        Guid transferId,
        SessionState session,
        CancellationToken ct = default)
    {
        var transfer = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transferId, ct);
        if (transfer is null)
            return OfficeMutationResult.Missing("재고이동 기록을 찾을 수 없습니다.");

        transfer.IsDeleted = true;
        transfer.IsDirty = true;
        transfer.UpdatedAtUtc = DateTime.UtcNow;
        transfer.LastSavedAtUtc = transfer.UpdatedAtUtc;

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalInventoryTransfer),
            EntityId = transferId.ToString("D"),
            Action = "Delete",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = string.Empty,
            AfterJson = string.Empty,
            CreatedAtUtc = DateTime.UtcNow
        });

        await _db.SaveChangesAsync(ct);

        await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
        {
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            ForceOverride = false
        }, ct);

        return OfficeMutationResult.Ok(transferId, "재고이동을 삭제했습니다.");
    }

    public async Task<OfficeMutationResult> RejectInventoryTransferAsync(
        Guid transferId,
        string rejectReason,
        SessionState session,
        CancellationToken ct = default)
    {
        var transfer = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transferId, ct);
        if (transfer is null)
            return OfficeMutationResult.Missing("재고이동 문서를 찾을 수 없습니다.");

        if (!CanReceiveInventoryTransfer(transfer, session))
            return OfficeMutationResult.Denied("도착지 담당자 또는 관리자만 재고이동을 반려할 수 있습니다.");

        if (string.Equals(transfer.TransferStatus, "수령확정", StringComparison.OrdinalIgnoreCase))
            return OfficeMutationResult.Denied("이미 수령확정된 문서는 반려할 수 없습니다.");

        if (string.IsNullOrWhiteSpace(rejectReason))
            return OfficeMutationResult.Denied("반려 사유를 입력하세요.");

        var now = DateTime.UtcNow;
        transfer.TransferStatus = "반려";
        transfer.RejectReason = rejectReason.Trim();
        transfer.RejectedByUsername = session.User?.Username ?? "local-user";
        transfer.RejectedAtUtc = now;
        transfer.LastStatusChangedByUsername = transfer.RejectedByUsername;
        transfer.LastStatusChangedAtUtc = now;
        transfer.LastSavedByUsername = session.User?.Username ?? "local-user";
        transfer.LastSavedAtUtc = now;
        transfer.IsDirty = true;
        transfer.UpdatedAtUtc = now;

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = nameof(LocalInventoryTransfer),
            EntityId = transferId.ToString("D"),
            Action = "Reject",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            BeforeJson = string.Empty,
            AfterJson = JsonSerializer.Serialize(new
            {
                transfer.TransferStatus,
                transfer.RejectReason,
                transfer.RejectedByUsername,
                transfer.RejectedAtUtc
            }, AuditJsonOptions),
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);
        await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
        {
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? DomainConstants.RoleUser,
            OfficeCode = session.OfficeCode,
            ForceOverride = false
        }, ct);
        return OfficeMutationResult.Ok(transferId, "재고이동을 반려 처리했습니다.");
    }

    // Inventory/Cost/Audit read models
    public Task<List<LocalItemWarehouseStock>> GetItemWarehouseStocksAsync(CancellationToken ct = default)
        => (
                from stock in _db.ItemWarehouseStocks.AsNoTracking()
                join item in _db.Items.IgnoreQueryFilters().AsNoTracking()
                    on stock.ItemId equals item.Id
                where !item.IsDeleted
                orderby stock.ItemId, stock.WarehouseCode
                select stock)
            .ToListAsync(ct);

    public Task<List<LocalInventoryMovement>> GetInventoryMovementsAsync(
        Guid itemId,
        int take = 200,
        CancellationToken ct = default)
        => _db.InventoryMovements
            .AsNoTracking()
            .Where(movement => movement.ItemId == itemId && movement.IsActive)
            .OrderByDescending(movement => movement.OccurredDate)
            .ThenByDescending(movement => movement.CreatedAtUtc)
            .Take(Math.Max(1, take))
            .ToListAsync(ct);

    public Task<List<LocalCostAllocation>> GetCostAllocationsForInvoiceAsync(Guid salesInvoiceId, CancellationToken ct = default)
        => _db.CostAllocations
            .AsNoTracking()
            .Where(allocation => allocation.SalesInvoiceId == salesInvoiceId)
            .OrderBy(allocation => allocation.CreatedAtUtc)
            .ToListAsync(ct);

    public Task<List<LocalAuditLog>> GetAuditLogsAsync(string entityName, string entityId, CancellationToken ct = default)
        => _db.AuditLogs
            .AsNoTracking()
            .Where(log => log.EntityName == entityName && log.EntityId == entityId)
            .OrderByDescending(log => log.CreatedAtUtc)
            .ToListAsync(ct);

    // Dirty-entity counts
    public async Task<int> CountDirtyAsync(CancellationToken ct = default)
    {
        var count = 0;
        count += await _db.CompanyProfiles.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.Customers.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.CustomerContracts.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.Items.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.ItemCategoryOptions.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.Invoices.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.Payments.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.Transactions.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.TransactionAttachments.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.InventoryTransfers.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.RentalManagementCompanies.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.RentalBillingProfiles.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.RentalAssets.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.RentalBillingLogs.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        return count;
    }

    private static InvoiceSaveContext NormalizeSaveContext(InvoiceSaveContext context)
    {
        return new InvoiceSaveContext
        {
            Username = string.IsNullOrWhiteSpace(context?.Username) ? "system" : context.Username.Trim(),
            Role = string.IsNullOrWhiteSpace(context?.Role) ? DomainConstants.RoleUser : context.Role.Trim(),
            OfficeCode = NormalizeOfficeCode(context?.OfficeCode, DomainConstants.OfficeUsenet),
            ForceOverride = context?.ForceOverride ?? false,
            ExpectedConcurrencyStamp = context?.ExpectedConcurrencyStamp
        };
    }

    private IQueryable<LocalCustomer> ApplyCustomerScope(
        IQueryable<LocalCustomer> query,
        SessionState session)
    {
        if (HasFullAccess(session))
            return query;

        var readableOfficeCodes = GetReadableOfficeCodes(session);
        var temporaryCustomerIds = _officeAccess.GetTemporaryCustomerAccessIds(session).ToList();
        return query.Where(customer =>
            customer.ResponsibleOfficeCode == OfficeCodeCatalog.Shared ||
            readableOfficeCodes.Contains(customer.ResponsibleOfficeCode) ||
            temporaryCustomerIds.Contains(customer.Id));
    }

    private IQueryable<LocalInvoice> ApplyInvoiceScope(
        IQueryable<LocalInvoice> query,
        SessionState session)
    {
        if (HasFullAccess(session))
            return query;

        var readableOfficeCodes = GetReadableOfficeCodes(session);
        var temporaryCustomerIds = _officeAccess.GetTemporaryCustomerAccessIds(session).ToList();
        return query.Where(invoice =>
            invoice.ResponsibleOfficeCode == OfficeCodeCatalog.Shared ||
            readableOfficeCodes.Contains(invoice.ResponsibleOfficeCode) ||
            temporaryCustomerIds.Contains(invoice.CustomerId));
    }

    private IQueryable<LocalTransaction> ApplyTransactionScope(
        IQueryable<LocalTransaction> query,
        SessionState session)
    {
        if (HasFullAccess(session))
            return query;

        var readableOfficeCodes = GetReadableOfficeCodes(session);
        var temporaryCustomerIds = _officeAccess.GetTemporaryCustomerAccessIds(session).ToList();
        return query.Where(transaction =>
            transaction.ResponsibleOfficeCode == OfficeCodeCatalog.Shared ||
            readableOfficeCodes.Contains(transaction.ResponsibleOfficeCode) ||
            temporaryCustomerIds.Contains(transaction.CustomerId));
    }

    private static bool HasFullAccess(SessionState? session)
        => session is null || !session.IsLoggedIn || session.HasGlobalDataScope;

    private static HashSet<string> GetReadableOfficeCodes(SessionState? session)
    {
        if (session is null || !session.IsLoggedIn || session.HasGlobalDataScope)
            return OfficeCodeCatalog.All.ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(session.ScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
        {
            return TenantScopeCatalog.GetOfficeCodesForTenant(session.TenantCode)
                .Select(code => NormalizeOfficeCode(code, DomainConstants.OfficeUsenet))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            NormalizeOfficeCode(session.OfficeCode, DomainConstants.OfficeUsenet)
        };
    }

    private static bool HasOfficeReadAccess(SessionState? session, string? officeCode)
    {
        var normalizedOfficeCode = NormalizeOfficeScope(officeCode, DomainConstants.OfficeUsenet);
        return IsSharedOfficeScope(normalizedOfficeCode) ||
               GetReadableOfficeCodes(session).Contains(normalizedOfficeCode);
    }

    private bool CanAccessCustomer(LocalCustomer customer, SessionState? session)
        => CanAccessCustomer(
            customer.Id,
            customer.ResponsibleOfficeCode,
            session,
            session?.User?.Role);

    private bool CanAccessCustomer(
        Guid customerId,
        string? customerOfficeCode,
        SessionState? session,
        string? role)
    {
        if (HasFullAccess(session) || DomainConstants.IsAdminRole(role))
            return true;

        var normalizedOfficeCode = NormalizeOfficeScope(customerOfficeCode, DomainConstants.OfficeUsenet);
        if (IsSharedOfficeScope(normalizedOfficeCode))
            return true;

        if (HasOfficeReadAccess(session, normalizedOfficeCode))
            return true;

        return session is not null && _officeAccess.HasTemporaryCustomerAccess(session, customerId);
    }

    private bool CanAccessInvoice(LocalInvoice invoice, SessionState? session)
    {
        if (HasFullAccess(session))
            return true;

        var officeCode = NormalizeOfficeScope(invoice.ResponsibleOfficeCode, DomainConstants.OfficeUsenet);
        if (IsSharedOfficeScope(officeCode))
            return true;

        if (HasOfficeReadAccess(session, officeCode))
            return true;

        return session is not null && _officeAccess.HasTemporaryCustomerAccess(session, invoice.CustomerId);
    }

    private bool CanAccessTransaction(LocalTransaction transaction, SessionState? session)
    {
        if (HasFullAccess(session))
            return true;

        var officeCode = NormalizeOfficeScope(transaction.ResponsibleOfficeCode, DomainConstants.OfficeUsenet);
        if (IsSharedOfficeScope(officeCode))
            return true;

        if (HasOfficeReadAccess(session, officeCode))
            return true;

        return session is not null && _officeAccess.HasTemporaryCustomerAccess(session, transaction.CustomerId);
    }

    private static string NormalizeOfficeCode(string? officeCode, string? fallback)
    {
        return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode, fallback);
    }

    private static string NormalizeOfficeScope(string? officeCode, string? fallback)
    {
        return OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(officeCode, fallback);
    }

    private static bool IsSharedOfficeScope(string? officeCode)
        => string.Equals(
            NormalizeOfficeScope(officeCode, OfficeCodeCatalog.Shared),
            OfficeCodeCatalog.Shared,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSystemOfficeCode(string? officeCode)
        => OfficeCodeCatalog.IsCanonicalOfficeCode(officeCode);

    private static string NormalizeWarehouseCode(string? warehouseCode, string? officeCode, string? fallbackOfficeCode)
        => OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(warehouseCode, officeCode, fallbackOfficeCode);

    private static int GetOfficeSortOrder(string? officeCode)
        => OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode) switch
        {
            var value when string.Equals(value, DomainConstants.OfficeUsenet, StringComparison.OrdinalIgnoreCase) => 0,
            var value when string.Equals(value, DomainConstants.OfficeItworld, StringComparison.OrdinalIgnoreCase) => 1,
            var value when string.Equals(value, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase) => 2,
            _ => 99
        };

    private static Guid ResolveVersionGroupId(LocalInvoice invoice, LocalInvoice? latest)
    {
        if (invoice.VersionGroupId != Guid.Empty)
            return invoice.VersionGroupId;

        if (latest is not null)
        {
            if (latest.VersionGroupId != Guid.Empty)
                return latest.VersionGroupId;

            return latest.Id;
        }

        return invoice.Id == Guid.Empty ? Guid.NewGuid() : invoice.Id;
    }

    private async Task<LocalInvoice?> ResolveLatestVersionAsync(LocalInvoice invoice, CancellationToken ct)
    {
        Guid? versionGroupId = invoice.VersionGroupId == Guid.Empty ? null : invoice.VersionGroupId;

        LocalInvoice? existingById = null;
        if (invoice.Id != Guid.Empty)
        {
            existingById = await _db.Invoices
                .Include(i => i.Lines)
                .Include(i => i.Payments)
                .FirstOrDefaultAsync(i => i.Id == invoice.Id, ct);

            if (existingById is not null && existingById.VersionGroupId != Guid.Empty)
                versionGroupId ??= existingById.VersionGroupId;
        }

        if (!versionGroupId.HasValue)
            return existingById;

        var latest = await _db.Invoices
            .Include(i => i.Lines)
            .Include(i => i.Payments)
            .FirstOrDefaultAsync(i => i.VersionGroupId == versionGroupId.Value && i.IsLatestVersion, ct);

        return latest ?? existingById;
    }

    private static List<LocalInvoiceLine> CloneLines(IEnumerable<LocalInvoiceLine> source, Guid invoiceId)
    {
        return source.Select(line => new LocalInvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            ItemId = line.ItemId,
            ItemNameOriginal = line.ItemNameOriginal ?? string.Empty,
            SpecificationOriginal = line.SpecificationOriginal ?? string.Empty,
            Unit = line.Unit ?? string.Empty,
            Quantity = line.Quantity,
            UnitPrice = line.UnitPrice,
            LineAmount = line.LineAmount,
            Remark = line.Remark ?? string.Empty,
            SerialNumber = line.SerialNumber ?? string.Empty,
            MaterialNumber = line.MaterialNumber ?? string.Empty,
            InstallLocation = line.InstallLocation ?? string.Empty,
            RentalStartDate = line.RentalStartDate,
            RentalEndDate = line.RentalEndDate,
            ItemTrackingType = ItemTrackingTypes.Normalize(line.ItemTrackingType),
            IsDeleted = false
        }).ToList();
    }

    private static List<LocalPayment> ClonePayments(IEnumerable<LocalPayment> source, Guid invoiceId, DateTime now)
    {
        return source.Select(payment => new LocalPayment
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            PaymentDate = payment.PaymentDate,
            Amount = payment.Amount,
            Note = payment.Note,
            IsDeleted = false,
            IsDirty = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = payment.Revision
        }).ToList();
    }

    private async Task<string> GenerateLocalTempNumberAsync(DateOnly invoiceDate, CancellationToken ct)
    {
        var yearMonth = invoiceDate.ToString("yyyyMM");
        var prefix = $"L{yearMonth}-";
        var count = await _db.Invoices
            .IgnoreQueryFilters()
            .CountAsync(invoice => invoice.LocalTempNumber.StartsWith(prefix), ct);

        return $"{prefix}{(count + 1):D4}";
    }

    private async Task<string> GenerateTransferNumberAsync(DateOnly transferDate, CancellationToken ct)
    {
        var yearMonth = transferDate.ToString("yyyyMM");
        var prefix = $"TR{yearMonth}-";
        var count = await _db.InventoryTransfers
            .IgnoreQueryFilters()
            .CountAsync(transfer => transfer.TransferNumber.StartsWith(prefix), ct);

        return $"{prefix}{(count + 1):D4}";
    }

    private static List<LocalInventoryTransferLine> CloneTransferLines(
        IEnumerable<LocalInventoryTransferLine> source,
        Guid transferId)
    {
        return source.Select(line => new LocalInventoryTransferLine
        {
            Id = line.Id == Guid.Empty ? Guid.NewGuid() : line.Id,
            TransferId = transferId,
            ItemId = line.ItemId,
            ItemNameOriginal = line.ItemNameOriginal ?? string.Empty,
            SpecificationOriginal = line.SpecificationOriginal ?? string.Empty,
            Unit = line.Unit ?? string.Empty,
            Quantity = line.Quantity,
            ReceivedQuantity = line.ReceivedQuantity ?? line.Quantity,
            QuantityDifference = line.QuantityDifference ?? ((line.ReceivedQuantity ?? line.Quantity) - line.Quantity),
            Remark = line.Remark ?? string.Empty,
            ReceiptRemark = line.ReceiptRemark ?? string.Empty,
            IsDeleted = false
        }).ToList();
    }

    private static bool IsFinalTransferStatus(string? status)
        => string.Equals(status, "수령확정", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(status, "반려", StringComparison.OrdinalIgnoreCase);

    private bool CanReceiveInventoryTransfer(LocalInventoryTransfer transfer, SessionState? session)
    {
        if (HasFullAccess(session))
            return true;

        var destinationOfficeCode = ResolveOfficeCodeFromWarehouseCode(transfer.ToWarehouseCode);
        var userOfficeCode = NormalizeOfficeCode(session?.OfficeCode, DomainConstants.OfficeUsenet);
        return string.Equals(destinationOfficeCode, userOfficeCode, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveOfficeCodeFromWarehouseCode(string? warehouseCode)
    {
        var normalizedWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeLoose(warehouseCode);
        return normalizedWarehouseCode switch
        {
            var value when string.Equals(value, DomainConstants.WarehouseItworldMain, StringComparison.OrdinalIgnoreCase) => DomainConstants.OfficeItworld,
            var value when string.Equals(value, DomainConstants.WarehouseYeonsuMain, StringComparison.OrdinalIgnoreCase) => DomainConstants.OfficeYeonsu,
            _ => DomainConstants.OfficeUsenet
        };
    }

    private static string NormalizeAttachmentType(string? attachmentType)
    {
        var normalized = (attachmentType ?? string.Empty).Trim();
        return normalized switch
        {
            "입금확인증" => "입금확인증",
            "영수증" => "영수증",
            "세금계산서" => "세금계산서",
            "계좌이체" => "계좌이체",
            "카드전표" => "카드전표",
            _ => "기타"
        };
    }

    private static string NormalizeCustomerContractType(string? contractType)
    {
        var normalized = (contractType ?? string.Empty).Trim();
        return normalized switch
        {
            "거래계약서" => "거래계약서",
            "렌탈계약서" => "렌탈계약서",
            "유지보수계약서" => "유지보수계약서",
            "특약서" => "특약서",
            _ => "기타"
        };
    }

    private static string NormalizeAttachmentVerificationStatus(string? verificationStatus)
    {
        var normalized = (verificationStatus ?? string.Empty).Trim();
        return normalized switch
        {
            "확인완료" => "확인완료",
            "반려" => "반려",
            _ => "미확인"
        };
    }

    private static string ResolveMimeType(string? extension)
    {
        var normalized = (extension ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".tif" or ".tiff" => "image/tiff",
            _ => "application/octet-stream"
        };
    }

    private static string ComputeFileHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash);
    }

    private static string ComputeFileHash(byte[] fileContent)
    {
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(fileContent);
        return Convert.ToHexString(hash);
    }

    private async Task ClearPrimaryCustomerContractAsync(
        Guid customerId,
        Guid? exceptContractId,
        CancellationToken ct)
    {
        var existingContracts = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == customerId &&
                              !current.IsDeleted &&
                              current.IsPrimary &&
                              (!exceptContractId.HasValue || current.Id != exceptContractId.Value))
            .ToListAsync(ct);

        foreach (var current in existingContracts)
        {
            current.IsPrimary = false;
            current.IsDirty = true;
            current.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private async Task SoftDeleteCustomerContractsAsync(
        Guid customerId,
        CancellationToken ct)
    {
        var contracts = await _db.CustomerContracts
            .IgnoreQueryFilters()
            .Where(current => current.CustomerId == customerId && !current.IsDeleted)
            .ToListAsync(ct);

        foreach (var contract in contracts)
        {
            contract.IsDeleted = true;
            contract.IsDirty = true;
            contract.IsPrimary = false;
            contract.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static object BuildAuditInvoice(LocalInvoice invoice)
    {
        return new
        {
            invoice.Id,
            invoice.VersionGroupId,
            invoice.VersionNumber,
            invoice.InvoiceNumber,
            invoice.LocalTempNumber,
            invoice.CustomerId,
            invoice.VoucherType,
            invoice.InvoiceDate,
            invoice.TotalAmount,
            invoice.SupplyAmount,
            invoice.VatAmount,
            invoice.Memo,
            invoice.ResponsibleOfficeCode,
            invoice.SourceWarehouseCode,
            invoice.DeliveryGroupId,
            invoice.ParentInvoiceId,
            invoice.IsLatestVersion,
            invoice.IsConfirmed,
            invoice.ConcurrencyStamp,
            invoice.CostStatus,
            Lines = invoice.Lines
                .Where(line => !line.IsDeleted)
                .Select(line => new
                {
                    line.Id,
                    line.ItemId,
                    line.ItemNameOriginal,
                    line.Quantity,
                    line.UnitPrice,
                    line.LineAmount,
                    line.SerialNumber
                })
                .ToList()
        };
    }

    private async Task RebuildInventorySnapshotsAsync(InvoiceSaveContext context, CancellationToken ct)
    {
        await _db.InventoryMovements.ExecuteDeleteAsync(ct);
        await _db.StockLayers.ExecuteDeleteAsync(ct);
        await _db.CostAllocations.ExecuteDeleteAsync(ct);
        await _db.ItemWarehouseStocks.ExecuteDeleteAsync(ct);
        await _db.SerialLedgers.ExecuteDeleteAsync(ct);
        await _db.InvoiceLineSerials.ExecuteDeleteAsync(ct);
        _db.ChangeTracker.Clear();

        var invoices = await _db.Invoices
            .Include(invoice => invoice.Lines.Where(line => !line.IsDeleted))
            .Where(invoice => !invoice.IsDeleted && invoice.IsLatestVersion && invoice.IsConfirmed)
            .OrderBy(invoice => invoice.InvoiceDate)
            .ThenBy(invoice => invoice.CreatedAtUtc)
            .ThenBy(invoice => invoice.VersionNumber)
            .ToListAsync(ct);
        var transfers = await _db.InventoryTransfers
            .Include(transfer => transfer.Lines.Where(line => !line.IsDeleted))
            .Where(transfer => !transfer.IsDeleted)
            .OrderBy(transfer => transfer.TransferDate)
            .ThenBy(transfer => transfer.CreatedAtUtc)
            .ToListAsync(ct);

        var stockMap = new Dictionary<(Guid ItemId, string WarehouseCode), decimal>();
        var layerMap = new Dictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>>();
        var serialMap = new Dictionary<string, LocalSerialLedger>(StringComparer.OrdinalIgnoreCase);
        var itemTrackingMap = await BuildItemTrackingMapAsync(ct);
        var timeline = invoices
            .Select(invoice => new InventoryTimelineEntry(
                invoice.InvoiceDate,
                invoice.LastSavedAtUtc == default ? invoice.CreatedAtUtc : invoice.LastSavedAtUtc,
                0,
                invoice,
                null))
            .Concat(transfers.Select(transfer => new InventoryTimelineEntry(
                transfer.TransferDate,
                transfer.LastSavedAtUtc == default ? transfer.CreatedAtUtc : transfer.LastSavedAtUtc,
                1,
                null,
                transfer)))
            .OrderBy(entry => entry.OccurredDate)
            .ThenBy(entry => entry.SortUtc)
            .ThenBy(entry => entry.Sequence)
            .ToList();

        foreach (var entry in timeline)
        {
            if (entry.Invoice is not null)
            {
                ApplyInvoiceInventoryEntry(entry.Invoice, context, stockMap, layerMap, serialMap, itemTrackingMap);
            }
            else if (entry.Transfer is not null)
            {
                ApplyInventoryTransferEntry(entry.Transfer, stockMap, layerMap);
            }
        }

        var normalizedStocks = stockMap
            .Select(entry => new
            {
                entry.Key.ItemId,
                WarehouseCode = NormalizeWarehouseCode(
                    entry.Key.WarehouseCode,
                    ResolveOfficeCodeFromWarehouseCode(entry.Key.WarehouseCode),
                    DomainConstants.OfficeUsenet),
                entry.Value
            })
            .GroupBy(entry => (entry.ItemId, entry.WarehouseCode))
            .Select(group => new
            {
                group.Key.ItemId,
                group.Key.WarehouseCode,
                Quantity = group.Sum(entry => entry.Value)
            })
            .ToList();

        foreach (var ledger in serialMap.Values)
            _db.SerialLedgers.Add(ledger);

        var itemStockTotals = normalizedStocks
            .GroupBy(entry => entry.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Quantity));

        var items = await _db.Items.ToListAsync(ct);
        foreach (var item in items)
        {
            var recalculatedStock = itemStockTotals.TryGetValue(item.Id, out var totalStock)
                ? totalStock
                : 0m;

            if (item.CurrentStock == recalculatedStock)
                continue;

            item.CurrentStock = recalculatedStock;
            item.IsDirty = true;
            item.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        foreach (var stock in normalizedStocks)
        {
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
INSERT INTO ItemWarehouseStocks (ItemId, WarehouseCode, Quantity, UpdatedAtUtc)
VALUES ({stock.ItemId}, {stock.WarehouseCode}, {stock.Quantity}, {DateTime.UtcNow})
ON CONFLICT(ItemId, WarehouseCode) DO UPDATE SET
    Quantity = excluded.Quantity,
    UpdatedAtUtc = excluded.UpdatedAtUtc;", ct);
        }

        RaiseInventoryStateChanged();
    }

    private async Task SyncItemWarehouseStocksAsync(
        Guid itemId,
        decimal currentStock,
        string? preferredOfficeCode,
        bool removeStocks,
        CancellationToken ct)
    {
        var stocks = await _db.ItemWarehouseStocks
            .Where(stock => stock.ItemId == itemId)
            .ToListAsync(ct);

        if (removeStocks)
        {
            if (stocks.Count > 0)
                _db.ItemWarehouseStocks.RemoveRange(stocks);
            return;
        }

        if (stocks.Count > 0)
            return;

        if (currentStock == 0m)
            return;

        _db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = ResolvePrimaryWarehouseCode(preferredOfficeCode),
            Quantity = currentStock,
            UpdatedAtUtc = DateTime.UtcNow
        });
    }

    private static string ResolvePrimaryWarehouseCode(string? preferredOfficeCode)
    {
        return OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(preferredOfficeCode, DomainConstants.OfficeUsenet) switch
        {
            OfficeCodeCatalog.Itworld => DomainConstants.WarehouseItworldMain,
            OfficeCodeCatalog.Yeonsu => DomainConstants.WarehouseYeonsuMain,
            _ => DomainConstants.WarehouseUsenetMain
        };
    }

    private static void NormalizeItemOperationalState(LocalItem item)
    {
        item.TrackingType = ItemOperationalPolicy.NormalizeTrackingType(item.TrackingType, item.ItemKind, item.CategoryName, item.IsRental);
        item.ItemKind = ItemOperationalPolicy.NormalizeItemKind(item.ItemKind, item.TrackingType, item.CategoryName, item.IsRental);

        switch (item.TrackingType)
        {
            case ItemTrackingTypes.Asset:
                item.IsRental = true;
                item.IsSale = false;
                item.CurrentStock = 0m;
                break;

            case ItemTrackingTypes.NonStock:
                item.IsRental = false;
                item.IsSale = true;
                item.CurrentStock = 0m;
                break;

            default:
                item.IsRental = false;
                item.IsSale = true;
                break;
        }
    }

    private async Task<string> EnsureItemCategoryOptionExistsAsync(string? categoryName, CancellationToken ct)
    {
        var normalizedName = SelectionOptionDefaults.NormalizeItemCategoryName(categoryName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return string.Empty;

        var normalizedKey = RentalCatalogValueNormalizer.NormalizeLooseKey(normalizedName);
        var options = _db.ItemCategoryOptions.Local
            .Concat(await _db.ItemCategoryOptions.IgnoreQueryFilters().ToListAsync(ct))
            .GroupBy(option => option.Id)
            .Select(group => group.First())
            .ToList();
        var existing = options.FirstOrDefault(option =>
            !option.IsDeleted &&
            string.Equals(RentalCatalogValueNormalizer.NormalizeLooseKey(option.Name), normalizedKey, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!string.Equals(existing.Name, normalizedName, StringComparison.Ordinal))
            {
                normalizedName = existing.Name;
            }

            if (!existing.IsActive)
            {
                existing.IsActive = true;
                existing.IsDeleted = false;
                existing.IsDirty = true;
                existing.UpdatedAtUtc = DateTime.UtcNow;
            }

            return normalizedName;
        }

        var now = DateTime.UtcNow;
        var nextSortOrder = options
            .Where(option => !option.IsDeleted)
            .Select(option => option.SortOrder)
            .DefaultIfEmpty(0)
            .Max() + 10;

        _db.ItemCategoryOptions.Add(new LocalItemCategoryOption
        {
            Id = Guid.NewGuid(),
            Name = normalizedName,
            SortOrder = nextSortOrder,
            IsSystemDefault = false,
            IsActive = true,
            IsDeleted = false,
            IsDirty = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        });

        return normalizedName;
    }

    private void RaiseInventoryStateChanged()
        => InventoryStateChanged?.Invoke(this, EventArgs.Empty);

    private async Task<Dictionary<Guid, string>> BuildItemTrackingMapAsync(CancellationToken ct)
    {
        return await _db.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => !item.IsDeleted)
            .ToDictionaryAsync(
                item => item.Id,
                item => ItemOperationalPolicy.NormalizeTrackingType(item.TrackingType, item.ItemKind, item.CategoryName, item.IsRental),
                ct);
    }

    private static string ResolveInvoiceLineTrackingType(
        LocalInvoiceLine line,
        IReadOnlyDictionary<Guid, string> itemTrackingMap)
    {
        if (!string.IsNullOrWhiteSpace(line.ItemTrackingType))
            return ItemTrackingTypes.Normalize(
                line.ItemTrackingType,
                line.ItemId.HasValue ? ItemTrackingTypes.Stock : ItemTrackingTypes.NonStock);

        if (line.ItemId.HasValue &&
            itemTrackingMap.TryGetValue(line.ItemId.Value, out var trackingType) &&
            !string.IsNullOrWhiteSpace(trackingType))
        {
            return ItemTrackingTypes.Normalize(trackingType);
        }

        return line.ItemId.HasValue
            ? ItemTrackingTypes.Stock
            : ItemTrackingTypes.NonStock;
    }

    private void ApplyInvoiceInventoryEntry(
        LocalInvoice invoice,
        InvoiceSaveContext context,
        IDictionary<(Guid ItemId, string WarehouseCode), decimal> stockMap,
        IDictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>> layerMap,
        IDictionary<string, LocalSerialLedger> serialMap,
        IReadOnlyDictionary<Guid, string> itemTrackingMap)
    {
        var warehouseCode = NormalizeWarehouseCode(
            invoice.SourceWarehouseCode,
            invoice.ResponsibleOfficeCode,
            context.OfficeCode);

        var invoiceHasUnsettledCost = false;

        foreach (var line in invoice.Lines)
        {
            if (line.ItemId is null)
                continue;

            var lineTrackingType = ResolveInvoiceLineTrackingType(line, itemTrackingMap);
            if (!string.Equals(line.ItemTrackingType, lineTrackingType, StringComparison.Ordinal))
                line.ItemTrackingType = lineTrackingType;
            if (!ItemOperationalPolicy.SupportsInventory(lineTrackingType))
                continue;

            var quantity = Math.Abs(line.Quantity);
            if (quantity <= 0)
                continue;

            var itemId = line.ItemId.Value;
            var key = (itemId, warehouseCode);
            EnsureStockKey(stockMap, key);

            var serialTokens = ParseSerialTokens(line.SerialNumber);
            foreach (var serial in serialTokens)
            {
                _db.InvoiceLineSerials.Add(new LocalInvoiceLineSerial
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoice.Id,
                    InvoiceLineId = line.Id,
                    ItemId = itemId,
                    SerialNumber = serial
                });
            }

            if (invoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement)
            {
                var inboundUnitCost = ResolveUnitCost(line);
                var layer = new LocalStockLayer
                {
                    Id = Guid.NewGuid(),
                    ItemId = itemId,
                    WarehouseCode = warehouseCode,
                    SourceInvoiceId = invoice.Id,
                    SourceInvoiceLineId = line.Id,
                    ReceiptDate = invoice.InvoiceDate,
                    UnitCost = inboundUnitCost,
                    OriginalQuantity = quantity,
                    RemainingQuantity = quantity,
                    IsNegativePlaceholder = false,
                    CreatedAtUtc = invoice.LastSavedAtUtc == default ? DateTime.UtcNow : invoice.LastSavedAtUtc
                };

                _db.StockLayers.Add(layer);
                EnsureLayerList(layerMap, key).Add(layer);
                stockMap[key] += quantity;

                _db.InventoryMovements.Add(new LocalInventoryMovement
                {
                    Id = Guid.NewGuid(),
                    InvoiceId = invoice.Id,
                    InvoiceLineId = line.Id,
                    ItemId = itemId,
                    WarehouseCode = warehouseCode,
                    MovementType = "PurchaseIn",
                    QuantityDelta = quantity,
                    UnitCost = inboundUnitCost,
                    Amount = Math.Round(quantity * inboundUnitCost, 2, MidpointRounding.AwayFromZero),
                    OccurredDate = invoice.InvoiceDate,
                    IsSettledCost = true,
                    IsActive = true,
                    Note = line.ItemNameOriginal,
                    CreatedByUsername = invoice.LastSavedByUsername,
                    CreatedAtUtc = invoice.LastSavedAtUtc == default ? DateTime.UtcNow : invoice.LastSavedAtUtc
                });

                foreach (var serial in serialTokens)
                {
                    var ledger = GetOrCreateSerialLedger(serialMap, serial);
                    ledger.ItemId = itemId;
                    ledger.WarehouseCode = warehouseCode;
                    ledger.Status = "InStock";
                    ledger.SourcePurchaseInvoiceId = invoice.Id;
                    ledger.LastInvoiceId = invoice.Id;
                    ledger.LastMovementType = "IN";
                    ledger.SourceSalesInvoiceId = null;
                    ledger.UpdatedAtUtc = DateTime.UtcNow;
                }
            }
            else if (invoice.VoucherType == VoucherType.Sales)
            {
                var outboundQuantity = quantity;
                var remaining = outboundQuantity;
                var lineSettled = true;

                var itemLayers = EnsureLayerList(layerMap, key);

                if (string.Equals(warehouseCode, DomainConstants.WarehouseYeonsuMain, StringComparison.OrdinalIgnoreCase))
                {
                    var availableInYeonsu = itemLayers
                        .Where(existingLayer => existingLayer.RemainingQuantity > 0)
                        .Sum(existingLayer => existingLayer.RemainingQuantity);

                    if (availableInYeonsu < remaining)
                    {
                        var requiredTransfer = remaining - availableInYeonsu;
                        AutoTransferFromUsenetToYeonsu(
                            invoice,
                            line,
                            itemId,
                            requiredTransfer,
                            stockMap,
                            layerMap);

                        itemLayers = EnsureLayerList(layerMap, key);
                    }
                }

                foreach (var layer in itemLayers
                             .Where(existingLayer => existingLayer.RemainingQuantity > 0)
                             .OrderBy(existingLayer => existingLayer.ReceiptDate)
                             .ThenBy(existingLayer => existingLayer.CreatedAtUtc)
                             .ToList())
                {
                    if (remaining <= 0)
                        break;

                    var consume = Math.Min(layer.RemainingQuantity, remaining);
                    if (consume <= 0)
                        continue;

                    layer.RemainingQuantity -= consume;
                    remaining -= consume;

                    _db.CostAllocations.Add(new LocalCostAllocation
                    {
                        Id = Guid.NewGuid(),
                        SalesInvoiceId = invoice.Id,
                        SalesInvoiceLineId = line.Id,
                        PurchaseInvoiceId = layer.SourceInvoiceId,
                        PurchaseInvoiceLineId = layer.SourceInvoiceLineId,
                        WarehouseCode = warehouseCode,
                        Quantity = consume,
                        UnitCost = layer.UnitCost,
                        CostAmount = Math.Round(consume * layer.UnitCost, 2, MidpointRounding.AwayFromZero),
                        IsUnsettled = false,
                        Note = line.ItemNameOriginal,
                        CreatedAtUtc = DateTime.UtcNow
                    });

                    _db.InventoryMovements.Add(new LocalInventoryMovement
                    {
                        Id = Guid.NewGuid(),
                        InvoiceId = invoice.Id,
                        InvoiceLineId = line.Id,
                        ItemId = itemId,
                        WarehouseCode = warehouseCode,
                        MovementType = "SalesOut",
                        QuantityDelta = -consume,
                        UnitCost = layer.UnitCost,
                        Amount = Math.Round(consume * layer.UnitCost, 2, MidpointRounding.AwayFromZero),
                        OccurredDate = invoice.InvoiceDate,
                        IsSettledCost = true,
                        IsActive = true,
                        Note = line.ItemNameOriginal,
                        CreatedByUsername = invoice.LastSavedByUsername,
                        CreatedAtUtc = invoice.LastSavedAtUtc == default ? DateTime.UtcNow : invoice.LastSavedAtUtc
                    });
                }

                if (remaining > 0)
                {
                    lineSettled = false;
                    invoiceHasUnsettledCost = true;

                    _db.CostAllocations.Add(new LocalCostAllocation
                    {
                        Id = Guid.NewGuid(),
                        SalesInvoiceId = invoice.Id,
                        SalesInvoiceLineId = line.Id,
                        PurchaseInvoiceId = null,
                        PurchaseInvoiceLineId = null,
                        WarehouseCode = warehouseCode,
                        Quantity = remaining,
                        UnitCost = 0m,
                        CostAmount = 0m,
                        IsUnsettled = true,
                        Note = "재고 부족(마이너스)로 인한 미정산",
                        CreatedAtUtc = DateTime.UtcNow
                    });

                    _db.InventoryMovements.Add(new LocalInventoryMovement
                    {
                        Id = Guid.NewGuid(),
                        InvoiceId = invoice.Id,
                        InvoiceLineId = line.Id,
                        ItemId = itemId,
                        WarehouseCode = warehouseCode,
                        MovementType = "SalesOut",
                        QuantityDelta = -remaining,
                        UnitCost = 0m,
                        Amount = 0m,
                        OccurredDate = invoice.InvoiceDate,
                        IsSettledCost = false,
                        IsActive = true,
                        Note = "재고 부족(마이너스)",
                        CreatedByUsername = invoice.LastSavedByUsername,
                        CreatedAtUtc = invoice.LastSavedAtUtc == default ? DateTime.UtcNow : invoice.LastSavedAtUtc
                    });
                }

                stockMap[key] -= outboundQuantity;

                foreach (var serial in serialTokens)
                {
                    var ledger = GetOrCreateSerialLedger(serialMap, serial);
                    ledger.ItemId ??= itemId;
                    ledger.WarehouseCode = warehouseCode;
                    ledger.Status = lineSettled ? "Outbound" : "PendingInboundOutbound";
                    ledger.SourceSalesInvoiceId = invoice.Id;
                    ledger.LastInvoiceId = invoice.Id;
                    ledger.LastMovementType = "OUT";
                    ledger.UpdatedAtUtc = DateTime.UtcNow;
                }
            }
        }

        if (invoice.VoucherType == VoucherType.Sales)
            invoice.CostStatus = invoiceHasUnsettledCost ? "Unsettled" : "Settled";
        else if (invoice.VoucherType is VoucherType.Purchase or VoucherType.Procurement)
            invoice.CostStatus = "Settled";
    }

        private void ApplyInventoryTransferEntry(
        LocalInventoryTransfer transfer,
        IDictionary<(Guid ItemId, string WarehouseCode), decimal> stockMap,
        IDictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>> layerMap)
    {
        var fromWarehouseCode = NormalizeWarehouseCode(transfer.FromWarehouseCode, DomainConstants.OfficeUsenet, DomainConstants.OfficeUsenet);
        var toWarehouseCode = NormalizeWarehouseCode(transfer.ToWarehouseCode, DomainConstants.OfficeYeonsu, DomainConstants.OfficeYeonsu);
        if (string.Equals(fromWarehouseCode, toWarehouseCode, StringComparison.OrdinalIgnoreCase))
            return;

        if (string.Equals(transfer.TransferStatus, "반려", StringComparison.OrdinalIgnoreCase))
            return;

        var isReceiptConfirmed = string.Equals(transfer.TransferStatus, "수령확정", StringComparison.OrdinalIgnoreCase);

        foreach (var line in transfer.Lines)
        {
            if (line.ItemId is null || line.Quantity <= 0m)
                continue;

            var itemId = line.ItemId.Value;
            var fromKey = (itemId, fromWarehouseCode);
            var toKey = (itemId, toWarehouseCode);
            EnsureStockKey(stockMap, fromKey);
            EnsureStockKey(stockMap, toKey);

            var sourceLayers = EnsureLayerList(layerMap, fromKey);
            var destinationLayers = EnsureLayerList(layerMap, toKey);
            var remainingRequested = line.Quantity;
            var remainingToReceive = isReceiptConfirmed
                ? Math.Min(line.Quantity, Math.Max(0m, line.ReceivedQuantity ?? line.Quantity))
                : 0m;
            var movementCreatedAt = transfer.LastSavedAtUtc == default ? DateTime.UtcNow : transfer.LastSavedAtUtc;
            var movementNote = string.IsNullOrWhiteSpace(line.Remark)
                ? $"{line.ItemNameOriginal} ({transfer.TransferNumber})"
                : $"{line.ItemNameOriginal} | {line.Remark}";

            foreach (var sourceLayer in sourceLayers
                         .Where(layer => layer.RemainingQuantity > 0)
                         .OrderBy(layer => layer.ReceiptDate)
                         .ThenBy(layer => layer.CreatedAtUtc)
                         .ToList())
            {
                if (remainingRequested <= 0)
                    break;

                var moveQuantity = Math.Min(sourceLayer.RemainingQuantity, remainingRequested);
                if (moveQuantity <= 0)
                    continue;

                sourceLayer.RemainingQuantity -= moveQuantity;
                remainingRequested -= moveQuantity;
                stockMap[fromKey] -= moveQuantity;

                var amount = Math.Round(moveQuantity * sourceLayer.UnitCost, 2, MidpointRounding.AwayFromZero);
                AddInventoryTransferOutMovement(
                    transfer,
                    itemId,
                    fromWarehouseCode,
                    moveQuantity,
                    sourceLayer.UnitCost,
                    amount,
                    isSettledCost: true,
                    movementNote,
                    movementCreatedAt);

                if (!isReceiptConfirmed || remainingToReceive <= 0)
                    continue;

                var receivedMoveQuantity = Math.Min(moveQuantity, remainingToReceive);
                remainingToReceive -= receivedMoveQuantity;
                stockMap[toKey] += receivedMoveQuantity;

                var destinationLayer = new LocalStockLayer
                {
                    Id = Guid.NewGuid(),
                    ItemId = itemId,
                    WarehouseCode = toWarehouseCode,
                    SourceInvoiceId = sourceLayer.SourceInvoiceId,
                    SourceInvoiceLineId = sourceLayer.SourceInvoiceLineId,
                    ReceiptDate = transfer.TransferDate,
                    UnitCost = sourceLayer.UnitCost,
                    OriginalQuantity = receivedMoveQuantity,
                    RemainingQuantity = receivedMoveQuantity,
                    IsNegativePlaceholder = false,
                    CreatedAtUtc = movementCreatedAt
                };

                _db.StockLayers.Add(destinationLayer);
                destinationLayers.Add(destinationLayer);

                AddInventoryTransferInMovement(
                    transfer,
                    itemId,
                    toWarehouseCode,
                    receivedMoveQuantity,
                    sourceLayer.UnitCost,
                    Math.Round(receivedMoveQuantity * sourceLayer.UnitCost, 2, MidpointRounding.AwayFromZero),
                    isSettledCost: true,
                    movementNote,
                    movementCreatedAt);
            }

            if (remainingRequested > 0)
            {
                stockMap[fromKey] -= remainingRequested;
                AddInventoryTransferOutMovement(
                    transfer,
                    itemId,
                    fromWarehouseCode,
                    remainingRequested,
                    0m,
                    0m,
                    isSettledCost: false,
                    $"{movementNote} | 재고 부족 이동",
                    movementCreatedAt);
            }

            if (isReceiptConfirmed && remainingToReceive > 0)
            {
                stockMap[toKey] += remainingToReceive;

                var placeholderLayer = new LocalStockLayer
                {
                    Id = Guid.NewGuid(),
                    ItemId = itemId,
                    WarehouseCode = toWarehouseCode,
                    SourceInvoiceId = null,
                    SourceInvoiceLineId = null,
                    ReceiptDate = transfer.TransferDate,
                    UnitCost = 0m,
                    OriginalQuantity = remainingToReceive,
                    RemainingQuantity = remainingToReceive,
                    IsNegativePlaceholder = true,
                    CreatedAtUtc = movementCreatedAt
                };

                _db.StockLayers.Add(placeholderLayer);
                destinationLayers.Add(placeholderLayer);

                AddInventoryTransferInMovement(
                    transfer,
                    itemId,
                    toWarehouseCode,
                    remainingToReceive,
                    0m,
                    0m,
                    isSettledCost: false,
                    $"{movementNote} | 수령확정 차이/미정산",
                    movementCreatedAt);
            }
        }
    }

    private void AddInventoryTransferOutMovement(
        LocalInventoryTransfer transfer,
        Guid itemId,
        string fromWarehouseCode,
        decimal quantity,
        decimal unitCost,
        decimal amount,
        bool isSettledCost,
        string note,
        DateTime createdAtUtc)
    {
        _db.InventoryMovements.Add(new LocalInventoryMovement
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            WarehouseCode = fromWarehouseCode,
            MovementType = "TransferOutManual",
            QuantityDelta = -quantity,
            UnitCost = unitCost,
            Amount = amount,
            OccurredDate = transfer.TransferDate,
            IsSettledCost = isSettledCost,
            IsActive = true,
            Note = note,
            CreatedByUsername = transfer.LastSavedByUsername,
            CreatedAtUtc = createdAtUtc
        });
    }

    private void AddInventoryTransferInMovement(
        LocalInventoryTransfer transfer,
        Guid itemId,
        string toWarehouseCode,
        decimal quantity,
        decimal unitCost,
        decimal amount,
        bool isSettledCost,
        string note,
        DateTime createdAtUtc)
    {
        _db.InventoryMovements.Add(new LocalInventoryMovement
        {
            Id = Guid.NewGuid(),
            ItemId = itemId,
            WarehouseCode = toWarehouseCode,
            MovementType = "TransferInManual",
            QuantityDelta = quantity,
            UnitCost = unitCost,
            Amount = amount,
            OccurredDate = transfer.TransferDate,
            IsSettledCost = isSettledCost,
            IsActive = true,
            Note = note,
            CreatedByUsername = transfer.LastSavedByUsername,
            CreatedAtUtc = createdAtUtc
        });
    }

    private static void EnsureStockKey(
        IDictionary<(Guid ItemId, string WarehouseCode), decimal> stockMap,
        (Guid ItemId, string WarehouseCode) key)
    {
        if (!stockMap.ContainsKey(key))
            stockMap[key] = 0m;
    }

    private static List<LocalStockLayer> EnsureLayerList(
        IDictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>> layerMap,
        (Guid ItemId, string WarehouseCode) key)
    {
        if (!layerMap.TryGetValue(key, out var layers))
        {
            layers = new List<LocalStockLayer>();
            layerMap[key] = layers;
        }

        return layers;
    }

    private static decimal ResolveUnitCost(LocalInvoiceLine line)
    {
        var quantity = Math.Abs(line.Quantity);
        if (quantity <= 0)
            return line.UnitPrice;

        if (line.LineAmount > 0)
            return Math.Round(line.LineAmount / quantity, 4, MidpointRounding.AwayFromZero);

        return line.UnitPrice;
    }

    private void AutoTransferFromUsenetToYeonsu(
        LocalInvoice salesInvoice,
        LocalInvoiceLine salesLine,
        Guid itemId,
        decimal requiredQuantity,
        IDictionary<(Guid ItemId, string WarehouseCode), decimal> stockMap,
        IDictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>> layerMap)
    {
        if (requiredQuantity <= 0)
            return;

        var fromWarehouseCode = DomainConstants.WarehouseUsenetMain;
        var toWarehouseCode = DomainConstants.WarehouseYeonsuMain;
        var fromKey = (itemId, fromWarehouseCode);
        var toKey = (itemId, toWarehouseCode);

        if (!layerMap.TryGetValue(fromKey, out var sourceLayers))
            return;

        if (!layerMap.TryGetValue(toKey, out var destinationLayers))
        {
            destinationLayers = new List<LocalStockLayer>();
            layerMap[toKey] = destinationLayers;
        }

        if (!stockMap.ContainsKey(fromKey))
            stockMap[fromKey] = 0m;
        if (!stockMap.ContainsKey(toKey))
            stockMap[toKey] = 0m;

        var availableNetInUsenet = Math.Max(stockMap[fromKey], 0m);
        if (availableNetInUsenet <= 0m)
            return;

        var candidates = sourceLayers
            .Where(layer => layer.RemainingQuantity > 0)
            .OrderBy(layer => layer.ReceiptDate)
            .ThenBy(layer => layer.CreatedAtUtc)
            .ToList();

        var transferRemaining = Math.Min(requiredQuantity, availableNetInUsenet);
        var movementCreatedAt = salesInvoice.LastSavedAtUtc == default
            ? DateTime.UtcNow
            : salesInvoice.LastSavedAtUtc;
        var referenceNumber = string.IsNullOrWhiteSpace(salesInvoice.InvoiceNumber)
            ? salesInvoice.LocalTempNumber
            : salesInvoice.InvoiceNumber;

        foreach (var sourceLayer in candidates)
        {
            if (transferRemaining <= 0)
                break;

            var moveQuantity = Math.Min(sourceLayer.RemainingQuantity, transferRemaining);
            if (moveQuantity <= 0)
                continue;

            sourceLayer.RemainingQuantity -= moveQuantity;
            transferRemaining -= moveQuantity;

            var destinationLayer = new LocalStockLayer
            {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                WarehouseCode = toWarehouseCode,
                SourceInvoiceId = sourceLayer.SourceInvoiceId,
                SourceInvoiceLineId = sourceLayer.SourceInvoiceLineId,
                ReceiptDate = salesInvoice.InvoiceDate,
                UnitCost = sourceLayer.UnitCost,
                OriginalQuantity = moveQuantity,
                RemainingQuantity = moveQuantity,
                IsNegativePlaceholder = false,
                CreatedAtUtc = movementCreatedAt
            };

            _db.StockLayers.Add(destinationLayer);
            destinationLayers.Add(destinationLayer);

            stockMap[fromKey] -= moveQuantity;
            stockMap[toKey] += moveQuantity;

            var amount = Math.Round(moveQuantity * sourceLayer.UnitCost, 2, MidpointRounding.AwayFromZero);
            var movementNote = string.IsNullOrWhiteSpace(referenceNumber)
                ? "?곗닔援??먮룞 ?ш퀬 ?대룞"
                : $"?곗닔援??먮룞 ?ш퀬 ?대룞 ({referenceNumber})";

            _db.InventoryMovements.Add(new LocalInventoryMovement
            {
                Id = Guid.NewGuid(),
                InvoiceId = salesInvoice.Id,
                InvoiceLineId = salesLine.Id,
                ItemId = itemId,
                WarehouseCode = fromWarehouseCode,
                MovementType = "TransferOutAuto",
                QuantityDelta = -moveQuantity,
                UnitCost = sourceLayer.UnitCost,
                Amount = amount,
                OccurredDate = salesInvoice.InvoiceDate,
                IsSettledCost = true,
                IsActive = true,
                Note = movementNote,
                CreatedByUsername = salesInvoice.LastSavedByUsername,
                CreatedAtUtc = movementCreatedAt
            });

            _db.InventoryMovements.Add(new LocalInventoryMovement
            {
                Id = Guid.NewGuid(),
                InvoiceId = salesInvoice.Id,
                InvoiceLineId = salesLine.Id,
                ItemId = itemId,
                WarehouseCode = toWarehouseCode,
                MovementType = "TransferInAuto",
                QuantityDelta = moveQuantity,
                UnitCost = sourceLayer.UnitCost,
                Amount = amount,
                OccurredDate = salesInvoice.InvoiceDate,
                IsSettledCost = true,
                IsActive = true,
                Note = movementNote,
                CreatedByUsername = salesInvoice.LastSavedByUsername,
                CreatedAtUtc = movementCreatedAt
            });
        }
    }

    private static LocalSerialLedger GetOrCreateSerialLedger(
        IDictionary<string, LocalSerialLedger> serialMap,
        string serialNumber)
    {
        if (serialMap.TryGetValue(serialNumber, out var existing))
            return existing;

        var ledger = new LocalSerialLedger
        {
            Id = Guid.NewGuid(),
            SerialNumber = serialNumber,
            WarehouseCode = string.Empty,
            Status = "Unknown",
            LastMovementType = string.Empty,
            UpdatedAtUtc = DateTime.UtcNow
        };

        serialMap[serialNumber] = ledger;
        return ledger;
    }

    private static List<string> ParseSerialTokens(string? serialText)
    {
        if (string.IsNullOrWhiteSpace(serialText))
            return new List<string>();

        var tokens = serialText
            .Split([',', ';', '|', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return tokens;
    }

    private static string NormalizeUsername(string? username)
        => (username ?? string.Empty).Trim().ToLowerInvariant();

    private static string GetCompanyProfileAssignmentKey(string username)
        => $"{CompanyProfileAssignmentPrefix}{NormalizeUsername(username)}";

    private sealed record InventoryTimelineEntry(
        DateOnly OccurredDate,
        DateTime SortUtc,
        int Sequence,
        LocalInvoice? Invoice,
        LocalInventoryTransfer? Transfer);
}
