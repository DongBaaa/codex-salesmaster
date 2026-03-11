using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Printing;
using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.Services;

/// <summary>
/// Facade over the local SQLite database for all CRUD operations.
/// All writes mark IsDirty = true; sync service flushes dirty records.
/// </summary>
public sealed class LocalStateService
{
    private const string YeonsuOfficeIdSettingKey = "SystemOffice.YeonsuOfficeId";
    private readonly LocalDbContext _db;
    private readonly OfficeAccessService _officeAccess;

    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public LocalStateService(LocalDbContext db, OfficeAccessService officeAccess)
    {
        _db = db;
        _officeAccess = officeAccess;
    }

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
        customer.ResponsibleOfficeCode = NormalizeOfficeCode(customer.ResponsibleOfficeCode, DomainConstants.OfficeUznet);
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

        var normalizedOfficeCode = NormalizeOfficeCode(customer.ResponsibleOfficeCode, DomainConstants.OfficeUznet);
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

        var grantedTemporaryAccess = !session.IsAdmin &&
                                     string.Equals(normalizedOfficeCode, DomainConstants.OfficeUznet, StringComparison.OrdinalIgnoreCase);

        if (grantedTemporaryAccess)
            _officeAccess.GrantTemporaryCustomerAccess(session, customer.Id);
        else
            _officeAccess.RevokeTemporaryCustomerAccess(session, customer.Id);

        return OfficeMutationResult.Ok(
            customer.Id,
            grantedTemporaryAccess
                ? "거래처를 저장했습니다. 유즈넷 거래처는 당일만 계속 작업할 수 있습니다."
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
        await _db.SaveChangesAsync(ct);
    }

    public async Task<OfficeMutationResult> DeleteCustomerAsync(
        Guid id,
        SessionState session,
        CancellationToken ct = default)
    {
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
        await _db.SaveChangesAsync(ct);
        _officeAccess.RevokeTemporaryCustomerAccess(session, id);

        return OfficeMutationResult.Ok(id, "거래처를 삭제했습니다.");
    }

    // Items
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
        var item = await _db.Items.FindAsync([id], ct);
        if (item is null)
            return;

        item.IsDeleted = true;
        item.IsDirty = true;
        item.UpdatedAtUtc = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // Offices & Warehouses
    public Task<List<LocalOffice>> GetOfficesAsync(CancellationToken ct = default)
        => _db.Offices.AsNoTracking().OrderBy(o => o.IsHeadOffice ? 0 : 1).ThenBy(o => o.Name).ToListAsync(ct);

    public async Task<OfficeMutationResult> SaveOfficeAsync(LocalOffice office, CancellationToken ct = default)
    {
        if (office is null)
            throw new ArgumentNullException(nameof(office));

        var code = NormalizeOfficeCode(office.Code, string.Empty);
        var name = (office.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code))
            return OfficeMutationResult.Denied("담당지점 코드를 입력하세요.");

        if (string.IsNullOrWhiteSpace(name))
            return OfficeMutationResult.Denied("담당지점 이름을 입력하세요.");

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
        var query = _db.Warehouses.AsNoTracking();
        if (onlyActive)
            query = query.Where(w => w.IsActive);

        return query.OrderBy(w => w.OfficeCode).ThenBy(w => w.Name).ToListAsync(ct);
    }

    public Task<List<LocalWarehouse>> GetWarehousesByOfficeAsync(string officeCode, bool onlyActive = true, CancellationToken ct = default)
    {
        var normalizedOfficeCode = NormalizeOfficeCode(officeCode, DomainConstants.OfficeUznet);
        var query = _db.Warehouses.AsNoTracking().Where(w => w.OfficeCode == normalizedOfficeCode);
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

        var headOffice = offices.FirstOrDefault(office => office.IsHeadOffice)
                         ?? offices.FirstOrDefault(office => string.Equals(office.Code, DomainConstants.OfficeUznet, StringComparison.OrdinalIgnoreCase))
                         ?? offices.FirstOrDefault();

        var yeonsuOfficeIdSetting = await _db.Settings.AsNoTracking()
            .FirstOrDefaultAsync(setting => setting.Key == YeonsuOfficeIdSettingKey, ct);

        LocalOffice? yeonsuOffice = null;
        if (yeonsuOfficeIdSetting is not null && Guid.TryParse(yeonsuOfficeIdSetting.Value, out var yeonsuOfficeId))
            yeonsuOffice = offices.FirstOrDefault(office => office.Id == yeonsuOfficeId);

        yeonsuOffice ??= offices.FirstOrDefault(office => !office.IsHeadOffice && office.Id != headOffice?.Id)
                         ?? offices.FirstOrDefault(office => office.Id != headOffice?.Id);

        DomainConstants.ConfigureSystemOffices(
            headOffice?.Code,
            yeonsuOffice?.Code,
            DomainConstants.WarehouseUznetMain,
            DomainConstants.WarehouseYeonsuMain);
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
        CancellationToken ct = default)
    {
        var query = _db.Invoices
            .Include(invoice => invoice.Lines.Where(line => !line.IsDeleted))
            .Include(invoice => invoice.Payments.Where(payment => !payment.IsDeleted))
            .AsNoTracking()
            .Where(invoice => !invoice.IsDeleted &&
                              invoice.IsLatestVersion &&
                              invoice.IsConfirmed &&
                              invoice.VoucherType == VoucherType.Sales &&
                              invoice.ResponsibleOfficeCode == DomainConstants.OfficeYeonsu);

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
        SessionState session,
        CancellationToken ct = default)
    {
        if (session.IsAdmin)
            return GetYeonsuDeliveryInvoicesAsync(from, to, customerId, warehouseCode, ct);

        return GetYeonsuDeliveryInvoicesAsync(from, to, customerId, warehouseCode, ct);
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
            OfficeCode = DomainConstants.OfficeUznet,
            ForceOverride = true
        };

        var result = await SaveInvoiceAsync(invoice, context, session: null, ct);
        if (!result.Success)
            throw new InvalidOperationException(result.Message);

        var saved = await GetInvoiceAsync(result.SavedInvoiceId, ct);
        if (saved is null)
            throw new InvalidOperationException("저장 후 전표를 다시 불러올 수 없습니다.");

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

        var context = NormalizeSaveContext(saveContext);
        var now = DateTime.UtcNow;

        var customer = await _db.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == invoice.CustomerId, ct);

        if (customer is null)
            return InvoiceSaveResult.Missing("거래처 정보를 찾을 수 없습니다.");

        var customerOfficeCode = NormalizeOfficeCode(customer.ResponsibleOfficeCode, DomainConstants.OfficeUznet);
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
            return InvoiceSaveResult.Conflict("다른 사용자가 먼저 저장했습니다. 최신 전표를 다시 불러온 뒤 재시도하세요.");
        }

        var versionGroupId = ResolveVersionGroupId(invoice, latest);
        if (latest is not null && latest.VersionGroupId == Guid.Empty)
            latest.VersionGroupId = versionGroupId;

        var targetInvoiceId = latest is null
            ? (invoice.Id == Guid.Empty ? Guid.NewGuid() : invoice.Id)
            : Guid.NewGuid();

        var versionNumber = (latest?.VersionNumber ?? 0) + 1;

        var sourceWarehouseCode = NormalizeWarehouseCode(
            invoice.SourceWarehouseCode,
            context.OfficeCode,
            customerOfficeCode);

        var responsibleOfficeCode = customerOfficeCode;

        var validLines = (invoice.Lines ?? new List<LocalInvoiceLine>())
            .Where(line => !line.IsDeleted && !string.IsNullOrWhiteSpace(line.ItemNameOriginal))
            .ToList();

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
            latest is null ? "전표를 저장했습니다." : $"전표 {newInvoice.VersionNumber}차 버전을 저장했습니다.");
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
            OfficeCode = DomainConstants.OfficeUznet,
            BeforeJson = string.Empty,
            AfterJson = string.Empty,
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);

        await RebuildInventorySnapshotsAsync(new InvoiceSaveContext
        {
            Username = "system",
            Role = DomainConstants.RoleAdmin,
            OfficeCode = DomainConstants.OfficeUznet,
            ForceOverride = true
        }, ct);
    }

    public async Task<OfficeMutationResult> DeleteInvoiceAsync(
        Guid id,
        SessionState session,
        CancellationToken ct = default)
    {
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
        var invoice = await _db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == payment.InvoiceId, ct);
        if (invoice is null)
            return OfficeMutationResult.Missing("전표를 찾을 수 없습니다.");

        if (!CanAccessInvoice(invoice, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 전표의 수금/지불을 저장할 수 없습니다.");

        payment.IsDirty = true;
        payment.UpdatedAtUtc = DateTime.UtcNow;

        var existing = await _db.Payments.FindAsync([payment.Id], ct);
        if (existing is null)
            _db.Payments.Add(payment);
        else
            _db.Entry(existing).CurrentValues.SetValues(payment);

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(payment.Id, "수금/지불을 저장했습니다.");
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
        string? officeCode = null,
        CancellationToken ct = default)
    {
        await SetSettingAsync("CachedSession_Username", username, ct);
        await SetSettingAsync("CachedSession_Role", role, ct);
        await SetSettingAsync("CachedSession_Permissions", string.Join(',', permissions), ct);
        await SetSettingAsync("CachedSession_OfficeCode", NormalizeOfficeCode(officeCode, DomainConstants.OfficeUznet), ct);
    }

    public async Task<UserSessionDto?> GetCachedSessionAsync(string username, CancellationToken ct = default)
    {
        var cachedUsername = await GetSettingAsync("CachedSession_Username", ct);
        if (!string.Equals(cachedUsername, username, StringComparison.OrdinalIgnoreCase))
            return null;

        var role = await GetSettingAsync("CachedSession_Role", ct) ?? "User";
        var permissionsRaw = await GetSettingAsync("CachedSession_Permissions", ct) ?? string.Empty;
        var permissions = permissionsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

        return new UserSessionDto
        {
            UserId = Guid.Empty,
            Username = cachedUsername ?? username,
            Role = role,
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

        var customer = await _db.Customers
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(current => current.Id == transaction.CustomerId, ct);
        if (customer is null)
            return OfficeMutationResult.Missing("거래처를 찾을 수 없습니다.");

        var customerOfficeCode = NormalizeOfficeCode(customer.ResponsibleOfficeCode, DomainConstants.OfficeUznet);
        if (!CanAccessCustomer(customer.Id, customerOfficeCode, session, session.User?.Role))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래처의 수금/지불을 저장할 수 없습니다.");

        var existing = await _db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(current => current.Id == transaction.Id, ct);
        if (existing is not null && !CanAccessTransaction(existing, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 거래처의 수금/지불을 저장할 수 없습니다.");

        transaction.ResponsibleOfficeCode = existing is null
            ? customerOfficeCode
            : NormalizeOfficeCode(existing.ResponsibleOfficeCode, customerOfficeCode);
        transaction.IsDirty = true;
        transaction.UpdatedAtUtc = DateTime.UtcNow;

        if (existing is null)
            _db.Transactions.Add(transaction);
        else
            _db.Entry(existing).CurrentValues.SetValues(transaction);

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(transaction.Id, "수금/지불을 저장했습니다.");
    }

    // Inventory/Cost/Audit read models
    public Task<List<LocalItemWarehouseStock>> GetItemWarehouseStocksAsync(CancellationToken ct = default)
        => _db.ItemWarehouseStocks
            .AsNoTracking()
            .OrderBy(stock => stock.ItemId)
            .ThenBy(stock => stock.WarehouseCode)
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
        count += await _db.Items.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.Invoices.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.Payments.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        count += await _db.Transactions.IgnoreQueryFilters().CountAsync(entity => entity.IsDirty, ct);
        return count;
    }

    private static InvoiceSaveContext NormalizeSaveContext(InvoiceSaveContext context)
    {
        return new InvoiceSaveContext
        {
            Username = string.IsNullOrWhiteSpace(context?.Username) ? "system" : context.Username.Trim(),
            Role = string.IsNullOrWhiteSpace(context?.Role) ? DomainConstants.RoleUser : context.Role.Trim(),
            OfficeCode = NormalizeOfficeCode(context?.OfficeCode, DomainConstants.OfficeUznet),
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

        var temporaryCustomerIds = _officeAccess.GetTemporaryCustomerAccessIds(session).ToList();
        return query.Where(customer =>
            customer.ResponsibleOfficeCode == DomainConstants.OfficeYeonsu ||
            temporaryCustomerIds.Contains(customer.Id));
    }

    private IQueryable<LocalInvoice> ApplyInvoiceScope(
        IQueryable<LocalInvoice> query,
        SessionState session)
    {
        if (HasFullAccess(session))
            return query;

        var temporaryCustomerIds = _officeAccess.GetTemporaryCustomerAccessIds(session).ToList();
        return query.Where(invoice =>
            invoice.ResponsibleOfficeCode == DomainConstants.OfficeYeonsu ||
            temporaryCustomerIds.Contains(invoice.CustomerId));
    }

    private IQueryable<LocalTransaction> ApplyTransactionScope(
        IQueryable<LocalTransaction> query,
        SessionState session)
    {
        if (HasFullAccess(session))
            return query;

        var temporaryCustomerIds = _officeAccess.GetTemporaryCustomerAccessIds(session).ToList();
        return query.Where(transaction =>
            transaction.ResponsibleOfficeCode == DomainConstants.OfficeYeonsu ||
            temporaryCustomerIds.Contains(transaction.CustomerId));
    }

    private static bool HasFullAccess(SessionState? session)
        => session is null || !session.IsLoggedIn || session.IsAdmin;

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

        var normalizedOfficeCode = NormalizeOfficeCode(customerOfficeCode, DomainConstants.OfficeUznet);
        if (string.Equals(normalizedOfficeCode, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase))
            return true;

        return session is not null && _officeAccess.HasTemporaryCustomerAccess(session, customerId);
    }

    private bool CanAccessInvoice(LocalInvoice invoice, SessionState? session)
    {
        if (HasFullAccess(session))
            return true;

        var officeCode = NormalizeOfficeCode(invoice.ResponsibleOfficeCode, DomainConstants.OfficeUznet);
        if (string.Equals(officeCode, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase))
            return true;

        return session is not null && _officeAccess.HasTemporaryCustomerAccess(session, invoice.CustomerId);
    }

    private bool CanAccessTransaction(LocalTransaction transaction, SessionState? session)
    {
        if (HasFullAccess(session))
            return true;

        var officeCode = NormalizeOfficeCode(transaction.ResponsibleOfficeCode, DomainConstants.OfficeUznet);
        if (string.Equals(officeCode, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase))
            return true;

        return session is not null && _officeAccess.HasTemporaryCustomerAccess(session, transaction.CustomerId);
    }

    private static string NormalizeOfficeCode(string? officeCode, string? fallback)
    {
        var normalized = (officeCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        return string.IsNullOrWhiteSpace(fallback)
            ? DomainConstants.OfficeUznet
            : fallback.Trim().ToUpperInvariant();
    }

    private static bool IsSystemOfficeCode(string? officeCode)
        => string.Equals(officeCode, DomainConstants.OfficeUznet, StringComparison.OrdinalIgnoreCase)
           || string.Equals(officeCode, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeWarehouseCode(string? warehouseCode, string? officeCode, string? fallbackOfficeCode)
    {
        var normalized = (warehouseCode ?? string.Empty).Trim().ToUpperInvariant();
        if (!string.IsNullOrWhiteSpace(normalized))
            return normalized;

        var office = NormalizeOfficeCode(officeCode, fallbackOfficeCode);
        return string.Equals(office, DomainConstants.OfficeYeonsu, StringComparison.OrdinalIgnoreCase)
            ? DomainConstants.WarehouseYeonsuMain
            : DomainConstants.WarehouseUznetMain;
    }

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
        var movements = await _db.InventoryMovements.ToListAsync(ct);
        var layers = await _db.StockLayers.ToListAsync(ct);
        var allocations = await _db.CostAllocations.ToListAsync(ct);
        var stocks = await _db.ItemWarehouseStocks.ToListAsync(ct);
        var serials = await _db.SerialLedgers.ToListAsync(ct);
        var lineSerials = await _db.InvoiceLineSerials.ToListAsync(ct);

        if (movements.Count > 0) _db.InventoryMovements.RemoveRange(movements);
        if (layers.Count > 0) _db.StockLayers.RemoveRange(layers);
        if (allocations.Count > 0) _db.CostAllocations.RemoveRange(allocations);
        if (stocks.Count > 0) _db.ItemWarehouseStocks.RemoveRange(stocks);
        if (serials.Count > 0) _db.SerialLedgers.RemoveRange(serials);
        if (lineSerials.Count > 0) _db.InvoiceLineSerials.RemoveRange(lineSerials);

        await _db.SaveChangesAsync(ct);

        var invoices = await _db.Invoices
            .Include(invoice => invoice.Lines.Where(line => !line.IsDeleted))
            .Where(invoice => !invoice.IsDeleted && invoice.IsLatestVersion && invoice.IsConfirmed)
            .OrderBy(invoice => invoice.InvoiceDate)
            .ThenBy(invoice => invoice.CreatedAtUtc)
            .ThenBy(invoice => invoice.VersionNumber)
            .ToListAsync(ct);

        var stockMap = new Dictionary<(Guid ItemId, string WarehouseCode), decimal>();
        var layerMap = new Dictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>>();
        var serialMap = new Dictionary<string, LocalSerialLedger>(StringComparer.OrdinalIgnoreCase);

        foreach (var invoice in invoices)
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

                var quantity = Math.Abs(line.Quantity);
                if (quantity <= 0)
                    continue;

                var itemId = line.ItemId.Value;
                var key = (itemId, warehouseCode);
                if (!stockMap.ContainsKey(key))
                    stockMap[key] = 0m;

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
                    if (!layerMap.TryGetValue(key, out var itemLayers))
                    {
                        itemLayers = new List<LocalStockLayer>();
                        layerMap[key] = itemLayers;
                    }

                    itemLayers.Add(layer);
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

                    if (!layerMap.TryGetValue(key, out var itemLayers))
                    {
                        itemLayers = new List<LocalStockLayer>();
                        layerMap[key] = itemLayers;
                    }

                    if (string.Equals(warehouseCode, DomainConstants.WarehouseYeonsuMain, StringComparison.OrdinalIgnoreCase))
                    {
                        var availableInYeonsu = itemLayers
                            .Where(existingLayer => existingLayer.RemainingQuantity > 0)
                            .Sum(existingLayer => existingLayer.RemainingQuantity);

                        if (availableInYeonsu < remaining)
                        {
                            var requiredTransfer = remaining - availableInYeonsu;
                            AutoTransferFromUznetToYeonsu(
                                invoice,
                                line,
                                itemId,
                                requiredTransfer,
                                stockMap,
                                layerMap);

                            if (!layerMap.TryGetValue(key, out itemLayers))
                            {
                                itemLayers = new List<LocalStockLayer>();
                                layerMap[key] = itemLayers;
                            }
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
                            Note = "재고 부족(마이너스)으로 원가 미확정",
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

        foreach (var stock in stockMap)
        {
            _db.ItemWarehouseStocks.Add(new LocalItemWarehouseStock
            {
                ItemId = stock.Key.ItemId,
                WarehouseCode = stock.Key.WarehouseCode,
                Quantity = stock.Value,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }

        foreach (var ledger in serialMap.Values)
            _db.SerialLedgers.Add(ledger);

        var itemStockTotals = stockMap
            .GroupBy(entry => entry.Key.ItemId)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Value));

        var items = await _db.Items.ToListAsync(ct);
        foreach (var item in items)
        {
            item.CurrentStock = itemStockTotals.TryGetValue(item.Id, out var totalStock)
                ? totalStock
                : 0m;

            item.IsDirty = true;
            item.UpdatedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
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

    private void AutoTransferFromUznetToYeonsu(
        LocalInvoice salesInvoice,
        LocalInvoiceLine salesLine,
        Guid itemId,
        decimal requiredQuantity,
        IDictionary<(Guid ItemId, string WarehouseCode), decimal> stockMap,
        IDictionary<(Guid ItemId, string WarehouseCode), List<LocalStockLayer>> layerMap)
    {
        if (requiredQuantity <= 0)
            return;

        var fromWarehouseCode = DomainConstants.WarehouseUznetMain;
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

        var availableNetInUznet = Math.Max(stockMap[fromKey], 0m);
        if (availableNetInUznet <= 0m)
            return;

        var candidates = sourceLayers
            .Where(layer => layer.RemainingQuantity > 0)
            .OrderBy(layer => layer.ReceiptDate)
            .ThenBy(layer => layer.CreatedAtUtc)
            .ToList();

        var transferRemaining = Math.Min(requiredQuantity, availableNetInUznet);
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
                ? "연수구 자동 재고 이동"
                : $"연수구 자동 재고 이동 ({referenceNumber})";

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
}
