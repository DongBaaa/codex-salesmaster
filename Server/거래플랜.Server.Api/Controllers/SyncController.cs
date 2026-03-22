using System.Text.Json;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("sync")]
public sealed class SyncController : ControllerBase
{
    private const long MaxContractFileSizeBytes = 15L * 1024 * 1024;
    private static readonly JsonSerializerOptions ConflictJsonOptions = new() { WriteIndented = false };

    private readonly AppDbContext _dbContext;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IInvoiceNumberService _invoiceNumberService;
    private readonly OfficeScopeService _officeScopeService;

    public SyncController(
        AppDbContext dbContext,
        ICurrentUserContext currentUserContext,
        IInvoiceNumberService invoiceNumberService,
        OfficeScopeService officeScopeService)
    {
        _dbContext = dbContext;
        _currentUserContext = currentUserContext;
        _invoiceNumberService = invoiceNumberService;
        _officeScopeService = officeScopeService;
    }

    [HttpGet("pull")]
    [ProducesResponseType(typeof(SyncPullResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SyncPullResponse>> Pull([FromQuery] long sinceRev, CancellationToken cancellationToken)
    {
        var response = new SyncPullResponse
        {
            CompanyProfiles = await _dbContext.CompanyProfiles.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Units = await _dbContext.Units.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            CustomerCategories = await _dbContext.CustomerCategories.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            PriceGradeOptions = await _dbContext.PriceGradeOptions.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            TradeTypeOptions = await _dbContext.TradeTypeOptions.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            ItemCategoryOptions = await _dbContext.ItemCategoryOptions.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.SortOrder).ThenBy(x => x.Name).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            CustomerMasters = await _officeScopeService.ApplyCustomerMasterScope(_dbContext.CustomerMasters.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Customers = await _officeScopeService.ApplyCustomerScope(_dbContext.Customers.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            CustomerContracts = await _officeScopeService.ApplyCustomerContractScope(_dbContext.CustomerContracts.IgnoreQueryFilters().AsNoTracking().Include(x => x.Customer))
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto(true)).ToListAsync(cancellationToken),
            Items = await _officeScopeService.ApplyItemScope(_dbContext.Items.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            ItemWarehouseStocks = await _officeScopeService.ApplyItemWarehouseStockScope(_dbContext.ItemWarehouseStocks.AsNoTracking())
                .OrderBy(x => x.ItemId).ThenBy(x => x.WarehouseCode)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Transactions = await _officeScopeService.ApplyTransactionScope(_dbContext.Transactions.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.TransactionDate).ThenBy(x => x.CreatedAtUtc)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            TransactionAttachments = await _officeScopeService.ApplyTransactionAttachmentScope(_dbContext.TransactionAttachments.IgnoreQueryFilters().AsNoTracking().Include(x => x.Transaction))
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.UploadedAtUtc).ThenBy(x => x.SortOrder)
                .Select(x => x.ToDto(true)).ToListAsync(cancellationToken),
            InventoryTransfers = await _officeScopeService.ApplyInventoryTransferScope(_dbContext.InventoryTransfers.IgnoreQueryFilters().AsNoTracking().Include(x => x.Lines))
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.TransferDate).ThenBy(x => x.CreatedAtUtc)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            RentalManagementCompanies = await _officeScopeService.ApplyRentalManagementCompanyScope(_dbContext.RentalManagementCompanies.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.Code).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            RentalBillingProfiles = await _officeScopeService.ApplyRentalBillingProfileScope(_dbContext.RentalBillingProfiles.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.CustomerName).ThenBy(x => x.ProfileKey)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            RentalAssets = await _officeScopeService.ApplyRentalAssetScope(_dbContext.RentalAssets.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.CustomerName).ThenBy(x => x.AssetKey)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            RentalBillingLogs = await _officeScopeService.ApplyRentalBillingLogScope(_dbContext.RentalBillingLogs.IgnoreQueryFilters().AsNoTracking())
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.ScheduledDate).ThenBy(x => x.BillingYearMonth)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Invoices = await _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices.IgnoreQueryFilters().Include(x => x.Customer).Include(x => x.Lines).Include(x => x.Payments).AsNoTracking())
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.CreatedAtUtc)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Payments = await _officeScopeService.ApplyPaymentScope(_dbContext.Payments.IgnoreQueryFilters().Include(x => x.Invoice).ThenInclude(invoice => invoice!.Customer).AsNoTracking())
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken)
        };

        response.CurrentServerRevision = await GetCurrentRevisionAsync(cancellationToken);
        return Ok(response);
    }

    [HttpPost("push")]
    [ProducesResponseType(typeof(SyncPushResult), StatusCodes.Status200OK)]
    public async Task<ActionResult<SyncPushResult>> Push([FromBody] SyncPushRequest request, CancellationToken cancellationToken)
    {
        var result = new SyncPushResult();

        await UpsertEntitiesAsync(request.CompanyProfiles ?? [], _dbContext.CompanyProfiles,
            (e, d) => e.Apply(d), d => new CompanyProfile { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        await UpsertEntitiesAsync(request.Units ?? [], _dbContext.Units,
            (e, d) => e.Apply(d), d => new Unit { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        await UpsertEntitiesAsync(request.CustomerCategories ?? [], _dbContext.CustomerCategories,
            (e, d) => e.Apply(d), d => new CustomerCategory { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        await UpsertPriceGradeOptionsAsync(request.PriceGradeOptions ?? [], result, cancellationToken);
        await UpsertTradeTypeOptionsAsync(request.TradeTypeOptions ?? [], result, cancellationToken);
        await UpsertItemCategoryOptionsAsync(request.ItemCategoryOptions ?? [], result, cancellationToken);
        var scopedCustomerMasters = await PrepareScopedCustomerMastersAsync(request.CustomerMasters ?? [], result, cancellationToken);
        var validCustomerMasters = await FilterValidCustomerMastersAsync(scopedCustomerMasters, result, cancellationToken);
        await UpsertEntitiesAsync(validCustomerMasters, _dbContext.CustomerMasters,
            (e, d) => e.Apply(d), d => new CustomerMaster { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        var scopedCustomers = await PrepareScopedCustomersAsync(request.Customers ?? [], result, cancellationToken);
        var validCustomers = await FilterValidCustomersAsync(scopedCustomers, result, cancellationToken);
        await UpsertEntitiesAsync(validCustomers, _dbContext.Customers,
            (e, d) => e.Apply(d), d => new Customer { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        await CascadeDeletedCustomerContractsAsync(validCustomers, cancellationToken);
        var validCustomerContracts = await FilterValidCustomerContractsAsync(request.CustomerContracts ?? [], result, cancellationToken);
        await UpsertEntitiesAsync(validCustomerContracts, _dbContext.CustomerContracts,
            (e, d) => e.Apply(d), d => new CustomerContract { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        var scopedItems = await PrepareScopedItemsAsync(request.Items ?? [], result, cancellationToken);
        await UpsertEntitiesAsync(scopedItems, _dbContext.Items,
            (e, d) => e.Apply(d), d => new Item { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        await UpsertItemWarehouseStocksAsync(request.ItemWarehouseStocks ?? [], cancellationToken);
        var scopedTransactions = await PrepareScopedTransactionsAsync(request.Transactions ?? [], result, cancellationToken);
        var validTransactions = await FilterValidTransactionsAsync(scopedTransactions, result, cancellationToken);
        await UpsertEntitiesAsync(validTransactions, _dbContext.Transactions,
            (e, d) => e.Apply(d), d => new TransactionRecord { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        var validTransactionAttachments = await FilterValidTransactionAttachmentsAsync(request.TransactionAttachments ?? [], result, cancellationToken);
        await UpsertEntitiesAsync(validTransactionAttachments, _dbContext.TransactionAttachments,
            (e, d) => e.Apply(d), d => new TransactionAttachment { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        var scopedInventoryTransfers = await PrepareScopedInventoryTransfersAsync(request.InventoryTransfers ?? [], result, cancellationToken);
        var validInventoryTransfers = await FilterValidInventoryTransfersAsync(scopedInventoryTransfers, result, cancellationToken);
        await UpsertInventoryTransfersAsync(validInventoryTransfers, result, cancellationToken);
        var scopedRentalCompanies = await PrepareScopedRentalManagementCompaniesAsync(request.RentalManagementCompanies ?? [], result, cancellationToken);
        await UpsertEntitiesAsync(scopedRentalCompanies, _dbContext.RentalManagementCompanies,
            (e, d) => e.Apply(d), d => new RentalManagementCompany { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        var scopedRentalProfiles = await PrepareScopedRentalBillingProfilesAsync(request.RentalBillingProfiles ?? [], result, cancellationToken);
        var validRentalProfiles = await FilterValidRentalBillingProfilesAsync(scopedRentalProfiles, result, cancellationToken);
        await UpsertEntitiesAsync(validRentalProfiles, _dbContext.RentalBillingProfiles,
            (e, d) => e.Apply(d), d => new RentalBillingProfile { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        var scopedRentalAssets = await PrepareScopedRentalAssetsAsync(request.RentalAssets ?? [], result, cancellationToken);
        var validRentalAssets = await FilterValidRentalAssetsAsync(scopedRentalAssets, result, cancellationToken);
        await UpsertEntitiesAsync(validRentalAssets, _dbContext.RentalAssets,
            (e, d) => e.Apply(d), d => new RentalAsset { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        var scopedRentalBillingLogs = await PrepareScopedRentalBillingLogsAsync(request.RentalBillingLogs ?? [], result, cancellationToken);
        var validRentalBillingLogs = await FilterValidRentalBillingLogsAsync(scopedRentalBillingLogs, result, cancellationToken);
        await UpsertEntitiesAsync(validRentalBillingLogs, _dbContext.RentalBillingLogs,
            (e, d) => e.Apply(d), d => new RentalBillingLog { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        var validInvoices = await FilterValidInvoicesAsync(request.Invoices ?? [], result, cancellationToken);
        await UpsertInvoicesAsync(validInvoices, result, cancellationToken);
        var validPayments = await FilterValidPaymentsAsync(request.Payments ?? [], result, cancellationToken);
        await UpsertEntitiesAsync(validPayments, _dbContext.Payments,
            (e, d) => e.Apply(d), d => new Payment { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        result.CurrentServerRevision = await GetCurrentRevisionAsync(cancellationToken);
        return Ok(result);
    }

    private async Task UpsertEntitiesAsync<TEntity, TDto>(
        IEnumerable<TDto> payload, DbSet<TEntity> dbSet,
        Action<TEntity, TDto> apply, Func<TDto, TEntity> create,
        SyncPushResult result, CancellationToken cancellationToken)
        where TEntity : TrackedEntity
        where TDto : SyncEntityDto
    {
        foreach (var dto in payload)
        {
            var entity = await dbSet.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (entity is null)
            {
                var newEntity = create(dto);
                apply(newEntity, dto);
                dbSet.Add(newEntity);
                result.AcceptedCount++;
                continue;
            }

            if (entity.UpdatedAtUtc > dto.UpdatedAtUtc)
            {
                var conflict = BuildConflict(dto, entity, typeof(TEntity).Name, "Server version is newer.");
                _dbContext.ConflictLogs.Add(conflict);
                continue;
            }

            apply(entity, dto);
            result.AcceptedCount++;
        }
    }

    private async Task UpsertPriceGradeOptionsAsync(
        IEnumerable<PriceGradeOptionDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        await UpsertSelectionOptionEntitiesAsync(
            payload,
            _dbContext.PriceGradeOptions,
            entity => entity.Name,
            dto => dto.Name,
            (entity, dto) => entity.Apply(dto),
            dto => new PriceGradeOption { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id },
            nameof(PriceGradeOption),
            result,
            cancellationToken);
    }

    private async Task UpsertTradeTypeOptionsAsync(
        IEnumerable<TradeTypeOptionDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        await UpsertSelectionOptionEntitiesAsync(
            payload,
            _dbContext.TradeTypeOptions,
            entity => entity.Name,
            dto => dto.Name,
            (entity, dto) => entity.Apply(dto),
            dto => new TradeTypeOption { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id },
            nameof(TradeTypeOption),
            result,
            cancellationToken);
    }

    private async Task UpsertItemCategoryOptionsAsync(
        IEnumerable<ItemCategoryOptionDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        await UpsertSelectionOptionEntitiesAsync(
            payload,
            _dbContext.ItemCategoryOptions,
            entity => entity.Name,
            dto => dto.Name,
            (entity, dto) => entity.Apply(dto),
            dto => new ItemCategoryOption { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id },
            nameof(ItemCategoryOption),
            result,
            cancellationToken);
    }

    private async Task UpsertSelectionOptionEntitiesAsync<TEntity, TDto>(
        IEnumerable<TDto> payload,
        DbSet<TEntity> dbSet,
        Func<TEntity, string> entityNameSelector,
        Func<TDto, string> dtoNameSelector,
        Action<TEntity, TDto> apply,
        Func<TDto, TEntity> create,
        string entityName,
        SyncPushResult result,
        CancellationToken cancellationToken)
        where TEntity : TrackedEntity
        where TDto : SyncEntityDto
    {
        var existingEntities = await dbSet.IgnoreQueryFilters().ToListAsync(cancellationToken);

        foreach (var dto in payload)
        {
            var normalizedName = NormalizeOptionName(dtoNameSelector(dto));
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                AddClientConflict(dto, entityName, "Option name is required.", result);
                continue;
            }

            var entity = existingEntities.FirstOrDefault(current => current.Id == dto.Id)
                ?? existingEntities.FirstOrDefault(current =>
                    string.Equals(NormalizeOptionName(entityNameSelector(current)), normalizedName, StringComparison.CurrentCultureIgnoreCase));

            if (entity is null)
            {
                var newEntity = create(dto);
                apply(newEntity, dto);
                dbSet.Add(newEntity);
                existingEntities.Add(newEntity);
                result.AcceptedCount++;
                continue;
            }

            if (entity.UpdatedAtUtc > dto.UpdatedAtUtc)
            {
                var conflict = BuildConflict(dto, entity, entityName, "Server version is newer.");
                _dbContext.ConflictLogs.Add(conflict);
                continue;
            }

            apply(entity, dto);
            result.AcceptedCount++;
        }
    }

    private async Task UpsertInvoicesAsync(IEnumerable<InvoiceDto> payload, SyncPushResult result, CancellationToken cancellationToken)
    {
        foreach (var dto in payload)
        {
            var entity = await _dbContext.Invoices.IgnoreQueryFilters()
                .Include(x => x.Customer)
                .Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);

            if (entity is null)
            {
                entity = new Invoice { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
                entity.Apply(dto);
                if (string.IsNullOrWhiteSpace(entity.InvoiceNumber))
                {
                    entity.InvoiceNumber = await _invoiceNumberService.GenerateAsync(entity.CustomerId, entity.InvoiceDate, cancellationToken);
                    result.AssignedInvoiceNumbers[dto.Id] = entity.InvoiceNumber;
                }

                ApplyInvoiceLines(entity, dto.Lines ?? []);
                _dbContext.Invoices.Add(entity);
                result.AcceptedCount++;
                continue;
            }

            if (!_officeScopeService.CanWriteOfficeForInvoices(entity.OfficeCode, entity.TenantCode))
            {
                AddClientConflict(dto, nameof(Invoice), "Current account cannot modify this office scope.", result);
                continue;
            }

            if (entity.UpdatedAtUtc > dto.UpdatedAtUtc)
            {
                var conflict = BuildConflict(dto, entity, nameof(Invoice), "Server version is newer.");
                _dbContext.ConflictLogs.Add(conflict);
                continue;
            }

            entity.Apply(dto);
            if (string.IsNullOrWhiteSpace(entity.InvoiceNumber))
            {
                entity.InvoiceNumber = await _invoiceNumberService.GenerateAsync(entity.CustomerId, entity.InvoiceDate, cancellationToken);
                result.AssignedInvoiceNumbers[dto.Id] = entity.InvoiceNumber;
            }

            _dbContext.InvoiceLines.RemoveRange(entity.Lines);
            entity.Lines.Clear();
            ApplyInvoiceLines(entity, dto.Lines ?? []);
            result.AcceptedCount++;
        }
    }

    private async Task<List<CustomerMasterDto>> PrepareScopedCustomerMastersAsync(
        IEnumerable<CustomerMasterDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<CustomerMasterDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.CustomerMasters.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForCustomers(existing.OfficeCode, existing.TenantCode))
            {
                AddClientConflict(dto, nameof(CustomerMaster), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode, existing?.TenantCode, existing?.OfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode, existing?.OfficeCode);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<CustomerMasterDto>> FilterValidCustomerMastersAsync(
        IEnumerable<CustomerMasterDto> payload, SyncPushResult result, CancellationToken cancellationToken)
    {
        var valid = new List<CustomerMasterDto>();

        foreach (var dto in payload)
        {
            if (dto.CategoryId.HasValue &&
                !await ExistsOrTrackedAsync(_dbContext.CustomerCategories, dto.CategoryId.Value, cancellationToken))
            {
                AddClientConflict(dto, nameof(CustomerMaster),
                    $"Referenced category was not found: {dto.CategoryId}.", result);
                continue;
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<CustomerDto>> PrepareScopedCustomersAsync(
        IEnumerable<CustomerDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<CustomerDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.Customers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForItems(existing.OfficeCode, existing.TenantCode))
            {
                AddClientConflict(dto, nameof(Customer), "Current account cannot modify this office scope.", result);
                continue;
            }

            if (existing is not null)
                PreserveCustomerTextWhenIncomingLooksLossy(dto, existing);

            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode, existing?.TenantCode, existing?.OfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode, existing?.OfficeCode);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<CustomerDto>> FilterValidCustomersAsync(
        IEnumerable<CustomerDto> payload, SyncPushResult result, CancellationToken cancellationToken)
    {
        var valid = new List<CustomerDto>();

        foreach (var dto in payload)
        {
            if (dto.CategoryId.HasValue &&
                !await ExistsOrTrackedAsync(_dbContext.CustomerCategories, dto.CategoryId.Value, cancellationToken))
            {
                AddClientConflict(dto, nameof(Customer),
                    $"Referenced category was not found: {dto.CategoryId}.", result);
                continue;
            }

            if (dto.CustomerMasterId.HasValue)
            {
                var customerMaster = await _dbContext.CustomerMasters.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.CustomerMasterId.Value, cancellationToken);
                if (customerMaster is null || customerMaster.IsDeleted)
                {
                    AddClientConflict(dto, nameof(Customer),
                        $"Referenced customer master was not found: {dto.CustomerMasterId}.", result);
                    continue;
                }

                if (!_officeScopeService.CanReadOfficeForCustomers(customerMaster.OfficeCode, customerMaster.TenantCode))
                {
                    AddClientConflict(dto, nameof(Customer),
                        $"Referenced customer master is outside the current office scope: {dto.CustomerMasterId}.", result);
                    continue;
                }
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<ItemDto>> PrepareScopedItemsAsync(
        IEnumerable<ItemDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<ItemDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.Items.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForCustomers(existing.OfficeCode, existing.TenantCode))
            {
                AddClientConflict(dto, nameof(Item), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode, existing?.TenantCode, existing?.OfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode, existing?.OfficeCode);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<TransactionDto>> PrepareScopedTransactionsAsync(
        IEnumerable<TransactionDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<TransactionDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.Transactions.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForPayments(existing.OfficeCode, existing.TenantCode))
            {
                AddClientConflict(dto, nameof(TransactionRecord), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode, existing?.TenantCode, existing?.OfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode, existing?.OfficeCode);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<TransactionDto>> FilterValidTransactionsAsync(
        IEnumerable<TransactionDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<TransactionDto>();

        foreach (var dto in payload)
        {
            var customer = await _dbContext.Customers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
            if (dto.CustomerId == Guid.Empty || customer is null || customer.IsDeleted)
            {
                AddClientConflict(dto, nameof(TransactionRecord),
                    $"Referenced customer was not found: {dto.CustomerId}.", result);
                continue;
            }

            if (!_officeScopeService.CanReadOfficeForCustomers(customer.OfficeCode, customer.TenantCode))
            {
                AddClientConflict(dto, nameof(TransactionRecord),
                    $"Referenced customer is outside the readable office scope: {dto.CustomerId}.", result);
                continue;
            }

            if (dto.LinkedInvoiceId.HasValue && dto.LinkedInvoiceId.Value != Guid.Empty)
            {
                var invoice = await _dbContext.Invoices.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.LinkedInvoiceId.Value, cancellationToken);
                if (invoice is null || invoice.IsDeleted)
                {
                    AddClientConflict(dto, nameof(TransactionRecord),
                        $"Referenced invoice was not found: {dto.LinkedInvoiceId}.", result);
                    continue;
                }

                if (!_officeScopeService.CanReadOfficeForInvoices(invoice.OfficeCode, invoice.TenantCode))
                {
                    AddClientConflict(dto, nameof(TransactionRecord),
                        $"Referenced invoice is outside the readable office scope: {dto.LinkedInvoiceId}.", result);
                    continue;
                }
            }

            if (dto.LinkedRentalBillingProfileId.HasValue && dto.LinkedRentalBillingProfileId.Value != Guid.Empty)
            {
                var profile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.LinkedRentalBillingProfileId.Value, cancellationToken);
                if (profile is null || profile.IsDeleted)
                {
                    AddClientConflict(dto, nameof(TransactionRecord),
                        $"Referenced rental billing profile was not found: {dto.LinkedRentalBillingProfileId}.", result);
                    continue;
                }

                if (!_officeScopeService.CanReadOfficeForRentals(profile.OfficeCode, profile.TenantCode))
                {
                    AddClientConflict(dto, nameof(TransactionRecord),
                        $"Referenced rental billing profile is outside the readable office scope: {dto.LinkedRentalBillingProfileId}.", result);
                    continue;
                }
            }

            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
                dto.TenantCode,
                dto.OfficeCode,
                customer.TenantCode,
                customer.OfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode, customer.OfficeCode);
            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<TransactionAttachmentDto>> FilterValidTransactionAttachmentsAsync(
        IEnumerable<TransactionAttachmentDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<TransactionAttachmentDto>();

        foreach (var dto in payload)
        {
            var transaction = await _dbContext.Transactions.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.TransactionId, cancellationToken);
            if (dto.TransactionId == Guid.Empty || transaction is null || transaction.IsDeleted)
            {
                AddClientConflict(dto, nameof(TransactionAttachment),
                    $"Referenced transaction was not found: {dto.TransactionId}.", result);
                continue;
            }

            if (!_officeScopeService.CanWriteOfficeForPayments(transaction.OfficeCode, transaction.TenantCode))
            {
                AddClientConflict(dto, nameof(TransactionAttachment),
                    $"Referenced transaction is outside the writable office scope: {dto.TransactionId}.", result);
                continue;
            }

            if (!dto.IsDeleted)
            {
                var fileContent = dto.FileContent ?? [];
                if (fileContent.Length == 0)
                {
                    AddClientConflict(dto, nameof(TransactionAttachment), "Attachment file content is required.", result);
                    continue;
                }

                dto.FileSize = dto.FileSize <= 0 ? fileContent.LongLength : dto.FileSize;
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<InventoryTransferDto>> PrepareScopedInventoryTransfersAsync(
        IEnumerable<InventoryTransferDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<InventoryTransferDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.InventoryTransfers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            var canWriteExisting = existing is null
                || _officeScopeService.CanWriteOfficeForDeliveries(existing.SourceOfficeCode, existing.TenantCode)
                || _officeScopeService.CanWriteOfficeForDeliveries(existing.TargetOfficeCode, existing.TenantCode);
            if (!canWriteExisting)
            {
                AddClientConflict(dto, nameof(InventoryTransfer), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
                dto.TenantCode,
                dto.SourceOfficeCode,
                existing?.TenantCode,
                existing?.SourceOfficeCode);
            dto.SourceOfficeCode = _officeScopeService.ResolveScopeForCreate(dto.SourceOfficeCode, existing?.SourceOfficeCode);
            dto.TargetOfficeCode = _officeScopeService.ResolveScopeForCreate(dto.TargetOfficeCode, existing?.TargetOfficeCode ?? dto.TargetOfficeCode);

            if (string.IsNullOrWhiteSpace(dto.FromWarehouseCode))
                dto.FromWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(dto.SourceOfficeCode);
            if (string.IsNullOrWhiteSpace(dto.ToWarehouseCode))
                dto.ToWarehouseCode = OfficeCodeCatalog.GetMainWarehouseCode(dto.TargetOfficeCode);

            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<InventoryTransferDto>> FilterValidInventoryTransfersAsync(
        IEnumerable<InventoryTransferDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<InventoryTransferDto>();

        foreach (var dto in payload)
        {
            var canAccessTransfer =
                _officeScopeService.CanWriteOfficeForDeliveries(dto.SourceOfficeCode, dto.TenantCode) ||
                _officeScopeService.CanWriteOfficeForDeliveries(dto.TargetOfficeCode, dto.TenantCode);
            if (!canAccessTransfer)
            {
                AddClientConflict(dto, nameof(InventoryTransfer),
                    "Current account cannot modify the source or target office scope.", result);
                continue;
            }

            var lines = dto.Lines ?? [];
            var lineConflict = false;
            foreach (var line in lines.Where(line => line.ItemId.HasValue && line.ItemId.Value != Guid.Empty))
            {
                var item = await _dbContext.Items.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == line.ItemId!.Value, cancellationToken);
                if (item is null || item.IsDeleted)
                {
                    AddClientConflict(dto, nameof(InventoryTransfer),
                        $"Referenced item was not found: {line.ItemId}.", result);
                    lineConflict = true;
                    break;
                }
            }

            if (lineConflict)
                continue;

            valid.Add(dto);
        }

        return valid;
    }

    private async Task UpsertInventoryTransfersAsync(
        IEnumerable<InventoryTransferDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        foreach (var dto in payload)
        {
            var entity = await _dbContext.InventoryTransfers.IgnoreQueryFilters()
                .Include(x => x.Lines)
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);

            if (entity is null)
            {
                entity = new InventoryTransfer { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
                entity.Apply(dto);
                ApplyInventoryTransferLines(entity, dto.Lines ?? []);
                _dbContext.InventoryTransfers.Add(entity);
                result.AcceptedCount++;
                continue;
            }

            var canWriteExisting =
                _officeScopeService.CanWriteOfficeForDeliveries(entity.SourceOfficeCode, entity.TenantCode) ||
                _officeScopeService.CanWriteOfficeForDeliveries(entity.TargetOfficeCode, entity.TenantCode);
            if (!canWriteExisting)
            {
                AddClientConflict(dto, nameof(InventoryTransfer), "Current account cannot modify this office scope.", result);
                continue;
            }

            if (entity.UpdatedAtUtc > dto.UpdatedAtUtc)
            {
                var conflict = BuildConflict(dto, entity, nameof(InventoryTransfer), "Server version is newer.");
                _dbContext.ConflictLogs.Add(conflict);
                continue;
            }

            entity.Apply(dto);
            _dbContext.InventoryTransferLines.RemoveRange(entity.Lines);
            entity.Lines.Clear();
            ApplyInventoryTransferLines(entity, dto.Lines ?? []);
            result.AcceptedCount++;
        }
    }

    private async Task<List<RentalManagementCompanyDto>> PrepareScopedRentalManagementCompaniesAsync(
        IEnumerable<RentalManagementCompanyDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<RentalManagementCompanyDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.RentalManagementCompanies.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.IsAdmin &&
                !_officeScopeService.CanReadOffice(_officeScopeService.CurrentOfficeCode, existing.TenantCode))
            {
                AddClientConflict(dto, nameof(RentalManagementCompany), "Current account cannot modify this tenant scope.", result);
                continue;
            }

            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, _officeScopeService.CurrentOfficeCode, existing?.TenantCode);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<RentalBillingProfileDto>> PrepareScopedRentalBillingProfilesAsync(
        IEnumerable<RentalBillingProfileDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<RentalBillingProfileDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForRentals(existing.OfficeCode, existing.TenantCode))
            {
                AddClientConflict(dto, nameof(RentalBillingProfile), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode, existing?.TenantCode, existing?.OfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode, existing?.OfficeCode);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<RentalBillingProfileDto>> FilterValidRentalBillingProfilesAsync(
        IEnumerable<RentalBillingProfileDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<RentalBillingProfileDto>();

        foreach (var dto in payload)
        {
            if (dto.CustomerId.HasValue && dto.CustomerId.Value != Guid.Empty)
            {
                var customer = await _dbContext.Customers.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.CustomerId.Value, cancellationToken);
                if (customer is null || customer.IsDeleted)
                {
                    AddClientConflict(dto, nameof(RentalBillingProfile),
                        $"Referenced customer was not found: {dto.CustomerId}.", result);
                    continue;
                }

                if (!_officeScopeService.CanReadOfficeForCustomers(customer.OfficeCode, customer.TenantCode))
                {
                    AddClientConflict(dto, nameof(RentalBillingProfile),
                        $"Referenced customer is outside the readable office scope: {dto.CustomerId}.", result);
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(dto.ManagementCompanyCode))
            {
                var managementCompanyCode = dto.ManagementCompanyCode.Trim();
                var exists = await _dbContext.RentalManagementCompanies.IgnoreQueryFilters()
                    .AnyAsync(x => x.TenantCode == dto.TenantCode && x.Code == managementCompanyCode && !x.IsDeleted, cancellationToken);
                if (!exists)
                {
                    AddClientConflict(dto, nameof(RentalBillingProfile),
                        $"Referenced management company was not found: {dto.ManagementCompanyCode}.", result);
                    continue;
                }
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<RentalAssetDto>> PrepareScopedRentalAssetsAsync(
        IEnumerable<RentalAssetDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<RentalAssetDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.RentalAssets.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForRentals(existing.OfficeCode, existing.TenantCode))
            {
                AddClientConflict(dto, nameof(RentalAsset), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode, existing?.TenantCode, existing?.OfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode, existing?.OfficeCode);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<RentalAssetDto>> FilterValidRentalAssetsAsync(
        IEnumerable<RentalAssetDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<RentalAssetDto>();

        foreach (var dto in payload)
        {
            if (dto.CustomerId.HasValue && dto.CustomerId.Value != Guid.Empty)
            {
                var customer = await _dbContext.Customers.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.CustomerId.Value, cancellationToken);
                if (customer is null || customer.IsDeleted)
                {
                    AddClientConflict(dto, nameof(RentalAsset),
                        $"Referenced customer was not found: {dto.CustomerId}.", result);
                    continue;
                }
            }

            if (dto.ItemId.HasValue && dto.ItemId.Value != Guid.Empty &&
                !await ExistsOrTrackedAsync(_dbContext.Items, dto.ItemId.Value, cancellationToken))
            {
                AddClientConflict(dto, nameof(RentalAsset),
                    $"Referenced item was not found: {dto.ItemId}.", result);
                continue;
            }

            if (dto.BillingProfileId.HasValue && dto.BillingProfileId.Value != Guid.Empty)
            {
                var billingProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.BillingProfileId.Value, cancellationToken);
                if (billingProfile is null || billingProfile.IsDeleted)
                {
                    AddClientConflict(dto, nameof(RentalAsset),
                        $"Referenced rental billing profile was not found: {dto.BillingProfileId}.", result);
                    continue;
                }
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<RentalBillingLogDto>> PrepareScopedRentalBillingLogsAsync(
        IEnumerable<RentalBillingLogDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var scoped = new List<RentalBillingLogDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.RentalBillingLogs.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForRentals(existing.OfficeCode, existing.TenantCode))
            {
                AddClientConflict(dto, nameof(RentalBillingLog), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(dto.TenantCode, dto.OfficeCode, existing?.TenantCode, existing?.OfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode, existing?.OfficeCode);
            scoped.Add(dto);
        }

        return scoped;
    }

    private async Task<List<RentalBillingLogDto>> FilterValidRentalBillingLogsAsync(
        IEnumerable<RentalBillingLogDto> payload,
        SyncPushResult result,
        CancellationToken cancellationToken)
    {
        var valid = new List<RentalBillingLogDto>();

        foreach (var dto in payload)
        {
            var billingProfile = await _dbContext.RentalBillingProfiles.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.BillingProfileId, cancellationToken);
            if (dto.BillingProfileId == Guid.Empty || billingProfile is null || billingProfile.IsDeleted)
            {
                AddClientConflict(dto, nameof(RentalBillingLog),
                    $"Referenced rental billing profile was not found: {dto.BillingProfileId}.", result);
                continue;
            }

            if (!_officeScopeService.CanReadOfficeForRentals(billingProfile.OfficeCode, billingProfile.TenantCode))
            {
                AddClientConflict(dto, nameof(RentalBillingLog),
                    $"Referenced rental billing profile is outside the readable office scope: {dto.BillingProfileId}.", result);
                continue;
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<InvoiceDto>> FilterValidInvoicesAsync(
        IEnumerable<InvoiceDto> payload, SyncPushResult result, CancellationToken cancellationToken)
    {
        var valid = new List<InvoiceDto>();

        foreach (var dto in payload)
        {
            var customer = await _dbContext.Customers.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
            if (dto.CustomerId == Guid.Empty || customer is null || customer.IsDeleted)
            {
                AddClientConflict(dto, nameof(Invoice),
                    $"Referenced customer was not found: {dto.CustomerId}.", result);
                continue;
            }

            if (!_officeScopeService.CanReadOfficeForCustomers(customer.OfficeCode, customer.TenantCode))
            {
                AddClientConflict(dto, nameof(Invoice),
                    $"Referenced customer is outside the readable office scope: {dto.CustomerId}.", result);
                continue;
            }

            var existing = await _dbContext.Invoices.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOfficeForInvoices(existing.OfficeCode, existing.TenantCode))
            {
                AddClientConflict(dto, nameof(Invoice),
                    "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
                dto.TenantCode,
                dto.OfficeCode,
                existing?.TenantCode ?? customer.TenantCode,
                existing?.OfficeCode ?? customer.OfficeCode);
            dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(
                dto.OfficeCode,
                existing?.OfficeCode ?? customer.OfficeCode);
            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<CustomerContractDto>> FilterValidCustomerContractsAsync(
        IEnumerable<CustomerContractDto> payload, SyncPushResult result, CancellationToken cancellationToken)
    {
        var valid = new List<CustomerContractDto>();

        foreach (var dto in payload)
        {
            var existing = await _dbContext.CustomerContracts.IgnoreQueryFilters()
                .Include(x => x.Customer)
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);

            if (existing?.Customer is not null && !_officeScopeService.CanWriteOfficeForContracts(existing.Customer.OfficeCode, existing.Customer.TenantCode))
            {
                AddClientConflict(dto, nameof(CustomerContract),
                    "Current account cannot modify this office scope.", result);
                continue;
            }

            if (!dto.IsDeleted)
            {
                var customer = await _dbContext.Customers.IgnoreQueryFilters()
                    .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
                if (dto.CustomerId == Guid.Empty || customer is null || customer.IsDeleted)
                {
                    AddClientConflict(dto, nameof(CustomerContract),
                        $"Referenced customer was not found: {dto.CustomerId}.", result);
                    continue;
                }

                if (!_officeScopeService.CanWriteOfficeForContracts(customer.OfficeCode, customer.TenantCode))
                {
                    AddClientConflict(dto, nameof(CustomerContract),
                        $"Referenced customer is outside the writable office scope: {dto.CustomerId}.", result);
                    continue;
                }
            }
            else if (existing is null)
            {
                AddClientConflict(dto, nameof(CustomerContract),
                    $"Referenced contract was not found: {dto.Id}.", result);
                continue;
            }

            if (!dto.IsDeleted)
            {
                var fileContent = dto.FileContent ?? [];
                var fileName = Path.GetFileName(dto.FileName ?? string.Empty);
                var mimeType = dto.MimeType?.Trim() ?? string.Empty;

                if (fileContent.Length == 0)
                {
                    AddClientConflict(dto, nameof(CustomerContract),
                        "Contract file content is required.", result);
                    continue;
                }

                if (fileContent.LongLength > MaxContractFileSizeBytes)
                {
                    AddClientConflict(dto, nameof(CustomerContract),
                        $"Contract file size exceeds the {MaxContractFileSizeBytes / (1024 * 1024)}MB limit.", result);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(fileName) ||
                    !string.Equals(Path.GetExtension(fileName), ".pdf", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                {
                    AddClientConflict(dto, nameof(CustomerContract),
                        "Only PDF contracts are allowed.", result);
                    continue;
                }
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task<List<PaymentDto>> FilterValidPaymentsAsync(
        IEnumerable<PaymentDto> payload, SyncPushResult result, CancellationToken cancellationToken)
    {
        var valid = new List<PaymentDto>();

        foreach (var dto in payload)
        {
            var invoice = await _dbContext.Invoices.IgnoreQueryFilters()
                .Include(x => x.Customer)
                .FirstOrDefaultAsync(x => x.Id == dto.InvoiceId, cancellationToken);
            if (dto.InvoiceId == Guid.Empty || invoice is null || invoice.IsDeleted)
            {
                AddClientConflict(dto, nameof(Payment),
                    $"Referenced invoice was not found: {dto.InvoiceId}.", result);
                continue;
            }

            if (!_officeScopeService.CanWriteOfficeForPayments(invoice.OfficeCode, invoice.TenantCode))
            {
                AddClientConflict(dto, nameof(Payment),
                    $"Referenced invoice is outside the writable office scope: {dto.InvoiceId}.", result);
                continue;
            }

            valid.Add(dto);
        }

        return valid;
    }

    private async Task UpsertItemWarehouseStocksAsync(
        IEnumerable<ItemWarehouseStockDto> payload,
        CancellationToken cancellationToken)
    {
        var sanitized = payload
            .Where(dto => dto.ItemId != Guid.Empty && !string.IsNullOrWhiteSpace(dto.WarehouseCode))
            .Select(dto => new ItemWarehouseStockDto
            {
                ItemId = dto.ItemId,
                WarehouseCode = dto.WarehouseCode.Trim(),
                Quantity = dto.Quantity,
                UpdatedAtUtc = NormalizeUtc(dto.UpdatedAtUtc)
            })
            .GroupBy(dto => new { dto.ItemId, dto.WarehouseCode })
            .Select(group => group.Last())
            .ToList();

        var groupedByItem = sanitized
            .GroupBy(dto => dto.ItemId)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var itemId in groupedByItem.Keys)
        {
            var scopedItem = await _dbContext.Items.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == itemId, cancellationToken);
            if (scopedItem is null || scopedItem.IsDeleted || !_officeScopeService.CanWriteOfficeForItems(scopedItem.OfficeCode, scopedItem.TenantCode))
                continue;

            var desiredCodes = groupedByItem[itemId]
                .Select(stock => stock.WarehouseCode)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var staleRows = await _officeScopeService.ApplyWarehouseScope(_dbContext.ItemWarehouseStocks)
                .Where(x => x.ItemId == itemId && !desiredCodes.Contains(x.WarehouseCode))
                .ToListAsync(cancellationToken);
            if (staleRows.Count > 0)
                _dbContext.ItemWarehouseStocks.RemoveRange(staleRows);
        }

        foreach (var dto in sanitized)
        {
            var item = await _dbContext.Items.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == dto.ItemId, cancellationToken);
            if (item is null || item.IsDeleted || !_officeScopeService.CanWriteOfficeForItems(item.OfficeCode, item.TenantCode) || !_officeScopeService.CanWriteWarehouse(dto.WarehouseCode, item.OfficeCode))
                continue;
            var entity = await _dbContext.ItemWarehouseStocks
                .FirstOrDefaultAsync(x => x.ItemId == dto.ItemId && x.WarehouseCode == dto.WarehouseCode, cancellationToken);

            if (entity is null)
            {
                _dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
                {
                    ItemId = dto.ItemId,
                    WarehouseCode = dto.WarehouseCode,
                    Quantity = dto.Quantity,
                    UpdatedAtUtc = NormalizeUtc(dto.UpdatedAtUtc)
                });
                continue;
            }

            entity.Quantity = dto.Quantity;
            entity.UpdatedAtUtc = NormalizeUtc(dto.UpdatedAtUtc);
        }
    }

    private static void ApplyInvoiceLines(Invoice invoice, IEnumerable<InvoiceLineDto> lines)
    {
        foreach (var line in lines)
        {
            var lineAmount = line.LineAmount == 0 ? line.Quantity * line.UnitPrice : line.LineAmount;
            invoice.Lines.Add(new InvoiceLine
            {
                Id = line.Id == Guid.Empty ? Guid.NewGuid() : line.Id,
                InvoiceId = invoice.Id, ItemId = line.ItemId,
                ItemNameOriginal = line.ItemNameOriginal, SpecificationOriginal = line.SpecificationOriginal,
                Unit = line.Unit, Quantity = line.Quantity, UnitPrice = line.UnitPrice,
                LineAmount = lineAmount, Remark = line.Remark,
                SerialNumber = line.SerialNumber, MaterialNumber = line.MaterialNumber,
                InstallLocation = line.InstallLocation, RentalStartDate = line.RentalStartDate,
                RentalEndDate = line.RentalEndDate
            });
        }
    }

    private static void ApplyInventoryTransferLines(InventoryTransfer transfer, IEnumerable<InventoryTransferLineDto> lines)
    {
        foreach (var line in lines)
        {
            transfer.Lines.Add(line.ToEntity(transfer.Id));
        }
    }

    private async Task<bool> ExistsOrTrackedAsync<TEntity>(
        DbSet<TEntity> dbSet, Guid id, CancellationToken cancellationToken)
        where TEntity : TrackedEntity
    {
        if (id == Guid.Empty)
        {
            return false;
        }

        if (dbSet.Local.Any(x => x.Id == id && !x.IsDeleted))
        {
            return true;
        }

        return await dbSet.IgnoreQueryFilters().AnyAsync(x => x.Id == id && !x.IsDeleted, cancellationToken);
    }

    private void AddClientConflict<TDto>(TDto client, string entityName, string reason, SyncPushResult result)
    {
        var entityId = client switch
        {
            SyncEntityDto dto => dto.Id.ToString(),
            _ => string.Empty
        };

        var conflict = new ConflictLog
        {
            UserId = _currentUserContext.UserId,
            Username = _currentUserContext.Username,
            EntityName = entityName,
            EntityId = entityId,
            ClientJson = JsonSerializer.Serialize(client, ConflictJsonOptions),
            ServerJson = string.Empty,
            Reason = reason,
            CreatedAtUtc = DateTime.UtcNow
        };

        _dbContext.ConflictLogs.Add(conflict);
        result.ConflictCount++;
        result.Conflicts.Add(conflict.ToDto());
    }

    private ConflictLog BuildConflict<TDto, TEntity>(TDto client, TEntity server, string entityName, string reason)
    {
        return new ConflictLog
        {
            UserId = _currentUserContext.UserId,
            Username = _currentUserContext.Username,
            EntityName = entityName,
            EntityId = server switch { TrackedEntity tracked => tracked.Id.ToString(), _ => string.Empty },
            ClientJson = JsonSerializer.Serialize(client, ConflictJsonOptions),
            ServerJson = SerializeConflictServerSnapshot(server),
            Reason = reason,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static string SerializeConflictServerSnapshot(object? server)
    {
        var snapshot = server switch
        {
            null => null,
            CompanyProfile entity => entity.ToDto(),
            Unit entity => entity.ToDto(),
            CustomerCategory entity => entity.ToDto(),
            CustomerMaster entity => entity.ToDto(),
            Customer entity => entity.ToDto(),
            CustomerContract entity => entity.ToDto(false),
            Item entity => entity.ToDto(),
            TransactionRecord entity => entity.ToDto(),
            TransactionAttachment entity => entity.ToDto(false),
            InventoryTransfer entity => entity.ToDto(),
            RentalManagementCompany entity => entity.ToDto(),
            RentalBillingProfile entity => entity.ToDto(),
            RentalAsset entity => entity.ToDto(),
            RentalBillingLog entity => entity.ToDto(),
            Invoice entity => entity.ToDto(),
            Payment entity => entity.ToDto(),
            _ => CreateScalarSnapshot(server)
        };

        return JsonSerializer.Serialize(snapshot, ConflictJsonOptions);
    }

    private static object CreateScalarSnapshot(object server)
    {
        var type = server.GetType();
        var dict = new Dictionary<string, object?>();

        foreach (var property in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            var propertyType = property.PropertyType;
            if (propertyType != typeof(string) &&
                typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType))
            {
                continue;
            }

            if (!propertyType.IsPrimitive &&
                propertyType != typeof(string) &&
                propertyType != typeof(Guid) && propertyType != typeof(Guid?) &&
                propertyType != typeof(DateTime) && propertyType != typeof(DateTime?) &&
                propertyType != typeof(DateOnly) && propertyType != typeof(DateOnly?) &&
                propertyType != typeof(decimal) && propertyType != typeof(decimal?) &&
                propertyType != typeof(int) && propertyType != typeof(int?) &&
                propertyType != typeof(long) && propertyType != typeof(long?) &&
                propertyType != typeof(bool) && propertyType != typeof(bool?))
            {
                continue;
            }

            dict[property.Name] = property.GetValue(server);
        }

        return dict;
    }

    private async Task CascadeDeletedCustomerContractsAsync(
        IEnumerable<CustomerDto> customers,
        CancellationToken cancellationToken)
    {
        var deletedCustomerIds = customers
            .Where(customer => customer.IsDeleted)
            .Select(customer => customer.Id)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();

        if (deletedCustomerIds.Count == 0)
            return;

        var contracts = await _dbContext.CustomerContracts
            .IgnoreQueryFilters()
            .Where(contract => deletedCustomerIds.Contains(contract.CustomerId) && !contract.IsDeleted)
            .ToListAsync(cancellationToken);

        foreach (var contract in contracts)
        {
            contract.IsDeleted = true;
            contract.IsPrimary = false;
            contract.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private async Task<long> GetCurrentRevisionAsync(CancellationToken cancellationToken)
    {
        var maxRevision = 0L;
        maxRevision = Math.Max(maxRevision, await _dbContext.CompanyProfiles.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Units.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.CustomerCategories.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.PriceGradeOptions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.TradeTypeOptions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.ItemCategoryOptions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.CustomerMasters.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Customers.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.CustomerContracts.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Items.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Transactions.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.TransactionAttachments.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.InventoryTransfers.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.RentalManagementCompanies.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.RentalBillingProfiles.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.RentalAssets.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.RentalBillingLogs.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Invoices.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Payments.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        return maxRevision;
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
            return DateTime.UtcNow;

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static string NormalizeOptionName(string? value)
        => (value ?? string.Empty).Trim();

    private static void PreserveCustomerTextWhenIncomingLooksLossy(CustomerDto dto, Customer existing)
    {
        var preservedName = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.NameOriginal, dto.NameOriginal);
        if (!string.Equals(preservedName, dto.NameOriginal, StringComparison.Ordinal))
        {
            dto.NameOriginal = preservedName;
            dto.NameMatchKey = MatchKeyNormalizer.Normalize(preservedName);
        }

        dto.TradeType = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.TradeType, dto.TradeType);
        dto.Department = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.Department, dto.Department);
        dto.ContactPerson = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.ContactPerson, dto.ContactPerson);
        dto.Address = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.Address, dto.Address);
        dto.Notes = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.Notes, dto.Notes);
        dto.Phone = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.Phone, dto.Phone);
        dto.Email = TextIntegrityGuard.PreferExistingIfIncomingLooksLossy(existing.Email, dto.Email);
    }
}




