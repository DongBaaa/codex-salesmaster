using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

public sealed class OperationalLogScopeService
{
    private readonly AppDbContext _dbContext;
    private readonly OfficeScopeService _officeScopeService;

    public OperationalLogScopeService(AppDbContext dbContext, OfficeScopeService officeScopeService)
    {
        _dbContext = dbContext;
        _officeScopeService = officeScopeService;
    }

    public async Task<List<AuditLog>> TakeVisibleAuditLogsAsync(
        IQueryable<AuditLog> orderedQuery,
        int requestedTake,
        CancellationToken cancellationToken)
        => await TakeVisibleLogsAsync(
            orderedQuery,
            requestedTake,
            maxTake: 1000,
            log => log.EntityName,
            log => log.EntityId,
            cancellationToken);

    public async Task<List<ConflictLog>> TakeVisibleConflictLogsAsync(
        IQueryable<ConflictLog> orderedQuery,
        int requestedTake,
        CancellationToken cancellationToken)
        => await TakeVisibleLogsAsync(
            orderedQuery,
            requestedTake,
            maxTake: 500,
            log => log.EntityName,
            log => log.EntityId,
            cancellationToken);

    public Task<bool> CanReadLogTargetAsync(string? entityName, string? entityId, CancellationToken cancellationToken)
    {
        if (_officeScopeService.HasGlobalDataScope)
            return Task.FromResult(true);

        return CanReadScopedLogTargetAsync(entityName, entityId, cancellationToken);
    }

    private async Task<List<TLog>> TakeVisibleLogsAsync<TLog>(
        IQueryable<TLog> orderedQuery,
        int requestedTake,
        int maxTake,
        Func<TLog, string?> entityNameSelector,
        Func<TLog, string?> entityIdSelector,
        CancellationToken cancellationToken)
        where TLog : class
    {
        var limit = NormalizeTake(requestedTake, maxTake);
        if (limit == 0)
            return [];

        if (_officeScopeService.HasGlobalDataScope)
            return await orderedQuery.Take(limit).ToListAsync(cancellationToken);

        var pageSize = Math.Clamp(limit * 4, 50, maxTake);
        var visible = new List<TLog>(limit);
        var offset = 0;

        while (visible.Count < limit)
        {
            var batch = await orderedQuery
                .Skip(offset)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            if (batch.Count == 0)
                break;

            foreach (var log in batch)
            {
                if (await CanReadScopedLogTargetAsync(entityNameSelector(log), entityIdSelector(log), cancellationToken))
                {
                    visible.Add(log);
                    if (visible.Count >= limit)
                        break;
                }
            }

            if (batch.Count < pageSize)
                break;

            offset += batch.Count;
        }

        return visible;
    }

    private static int NormalizeTake(int requestedTake, int maxTake)
        => requestedTake <= 0 ? 0 : Math.Min(requestedTake, maxTake);

    private async Task<bool> CanReadScopedLogTargetAsync(
        string? entityName,
        string? entityId,
        CancellationToken cancellationToken)
    {
        var normalizedEntityName = NormalizeEntityName(entityName);
        var normalizedEntityId = (entityId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedEntityName) || string.IsNullOrWhiteSpace(normalizedEntityId))
            return false;

        if (string.Equals(normalizedEntityName, nameof(ItemWarehouseStock), StringComparison.Ordinal))
            return await CanReadItemWarehouseStockLogAsync(normalizedEntityId, cancellationToken);

        if (!Guid.TryParse(normalizedEntityId, out var id))
            return false;

        return normalizedEntityName switch
        {
            nameof(UserAccount) => await CanReadUserLogAsync(id, cancellationToken),
            nameof(TenantDefinition) => await CanReadTenantDefinitionLogAsync(id, cancellationToken),
            nameof(TenantOfficeDefinition) => await CanReadTenantOfficeDefinitionLogAsync(id, cancellationToken),
            nameof(DataSharingPolicy) => await CanReadDataSharingPolicyLogAsync(id, cancellationToken),
            nameof(CompanyProfile) => await _officeScopeService.ApplyCompanyProfileScope(
                    _dbContext.CompanyProfiles.IgnoreQueryFilters().AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(Unit) or nameof(CustomerCategory) or nameof(PriceGradeOption) or
                nameof(TradeTypeOption) or nameof(ItemCategoryOption) => true,
            nameof(CustomerMaster) => await _officeScopeService.ApplyCustomerMasterScope(
                    _dbContext.CustomerMasters.IgnoreQueryFilters().AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(Customer) => await _officeScopeService.ApplyCustomerScope(
                    _dbContext.Customers.IgnoreQueryFilters().AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(CustomerContract) => await _officeScopeService.ApplyCustomerContractScope(
                    _dbContext.CustomerContracts.IgnoreQueryFilters().Include(entity => entity.Customer).AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(Item) => await _officeScopeService.ApplyItemScope(
                    _dbContext.Items.IgnoreQueryFilters().AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(Invoice) => await _officeScopeService.ApplyInvoiceScope(
                    _dbContext.Invoices.IgnoreQueryFilters().AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(Payment) => await _officeScopeService.ApplyPaymentScope(
                    _dbContext.Payments.IgnoreQueryFilters().Include(entity => entity.Invoice).AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(PaymentAttachment) => await _officeScopeService.ApplyPaymentScope(
                    _dbContext.Payments.IgnoreQueryFilters()
                        .Include(entity => entity.Invoice)
                        .Where(entity => entity.Attachments.Any(attachment => attachment.Id == id))
                        .AsNoTracking())
                .AnyAsync(cancellationToken),
            nameof(TransactionRecord) => await _officeScopeService.ApplyTransactionScope(
                    _dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(TransactionAttachment) => await _officeScopeService.ApplyTransactionScope(
                    _dbContext.Transactions.IgnoreQueryFilters()
                        .Where(entity => entity.Attachments.Any(attachment => attachment.Id == id))
                        .AsNoTracking())
                .AnyAsync(cancellationToken),
            nameof(InventoryTransfer) => await _officeScopeService.ApplyInventoryTransferScope(
                    _dbContext.InventoryTransfers.IgnoreQueryFilters().AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(RentalManagementCompany) => await _officeScopeService.ApplyRentalManagementCompanyScope(
                    _dbContext.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(RentalBillingProfile) => await _officeScopeService.ApplyRentalBillingProfileScope(
                    _dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(RentalAsset) => await _officeScopeService.ApplyRentalAssetScope(
                    _dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(RentalAssetAssignmentHistory) => await _officeScopeService.ApplyRentalAssignmentHistoryScope(
                    _dbContext.RentalAssetAssignmentHistories.IgnoreQueryFilters().AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            nameof(RentalBillingLog) => await _officeScopeService.ApplyRentalBillingLogScope(
                    _dbContext.RentalBillingLogs.IgnoreQueryFilters().AsNoTracking())
                .AnyAsync(entity => entity.Id == id, cancellationToken),
            _ => false
        };
    }

    private async Task<bool> CanReadItemWarehouseStockLogAsync(
        string entityId,
        CancellationToken cancellationToken)
    {
        var parts = entityId.Split('|', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out var itemId))
            return false;

        var warehouseCode = parts[1];
        if (string.IsNullOrWhiteSpace(warehouseCode))
            return false;

        return await _officeScopeService.ApplyItemWarehouseStockScope(
                _dbContext.ItemWarehouseStocks
                    .Include(entity => entity.Item)
                    .AsNoTracking())
            .AnyAsync(entity => entity.ItemId == itemId && entity.WarehouseCode == warehouseCode, cancellationToken);
    }

    private async Task<bool> CanReadUserLogAsync(Guid id, CancellationToken cancellationToken)
    {
        var user = await _dbContext.Users
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => entity.Id == id)
            .Select(entity => new { entity.TenantCode, entity.OfficeCode })
            .FirstOrDefaultAsync(cancellationToken);

        return user is not null &&
               _officeScopeService.CanReadOffice(user.OfficeCode, user.TenantCode);
    }

    private async Task<bool> CanReadTenantDefinitionLogAsync(Guid id, CancellationToken cancellationToken)
    {
        var tenant = await _dbContext.TenantDefinitions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => entity.Id == id)
            .Select(entity => entity.TenantCode)
            .FirstOrDefaultAsync(cancellationToken);

        return !string.IsNullOrWhiteSpace(tenant) &&
               string.Equals(
                   TenantScopeCatalog.NormalizeTenantCodeOrDefault(tenant),
                   _officeScopeService.CurrentTenantCode,
                   StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> CanReadTenantOfficeDefinitionLogAsync(Guid id, CancellationToken cancellationToken)
    {
        var office = await _dbContext.TenantOfficeDefinitions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => entity.Id == id)
            .Select(entity => new { entity.TenantCode, entity.OfficeCode })
            .FirstOrDefaultAsync(cancellationToken);

        return office is not null &&
               _officeScopeService.CanReadOffice(office.OfficeCode, office.TenantCode);
    }

    private async Task<bool> CanReadDataSharingPolicyLogAsync(Guid id, CancellationToken cancellationToken)
    {
        var policy = await _dbContext.DataSharingPolicies
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(entity => entity.Id == id)
            .Select(entity => new
            {
                entity.SourceTenantCode,
                entity.SourceOfficeCode,
                entity.TargetTenantCode,
                entity.TargetOfficeCode
            })
            .FirstOrDefaultAsync(cancellationToken);

        return policy is not null &&
               (_officeScopeService.CanReadOffice(policy.SourceOfficeCode, policy.SourceTenantCode) ||
                _officeScopeService.CanReadOffice(policy.TargetOfficeCode, policy.TargetTenantCode));
    }

    private static string NormalizeEntityName(string? entityName)
    {
        var normalized = (entityName ?? string.Empty).Trim();
        var lastDotIndex = normalized.LastIndexOf('.');
        return lastDotIndex >= 0 && lastDotIndex < normalized.Length - 1
            ? normalized[(lastDotIndex + 1)..]
            : normalized;
    }
}
