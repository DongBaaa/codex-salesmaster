using System.Text.Json;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Services;
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
                result.ConflictCount++;
                var conflict = BuildConflict(dto, entity, typeof(TEntity).Name, "Server version is newer.");
                _dbContext.ConflictLogs.Add(conflict);
                result.Conflicts.Add(conflict.ToDto());
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

            if (!_officeScopeService.CanWriteOffice(entity.OfficeCode))
            {
                AddClientConflict(dto, nameof(Invoice), "Current account cannot modify this office scope.", result);
                continue;
            }

            if (entity.UpdatedAtUtc > dto.UpdatedAtUtc)
            {
                result.ConflictCount++;
                var conflict = BuildConflict(dto, entity, nameof(Invoice), "Server version is newer.");
                _dbContext.ConflictLogs.Add(conflict);
                result.Conflicts.Add(conflict.ToDto());
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
            if (existing is not null && !_officeScopeService.CanWriteOffice(existing.OfficeCode))
            {
                AddClientConflict(dto, nameof(CustomerMaster), "Current account cannot modify this office scope.", result);
                continue;
            }

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
            if (existing is not null && !_officeScopeService.CanWriteOffice(existing.OfficeCode))
            {
                AddClientConflict(dto, nameof(Customer), "Current account cannot modify this office scope.", result);
                continue;
            }

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

                if (!_officeScopeService.CanReadOffice(customerMaster.OfficeCode))
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
            if (existing is not null && !_officeScopeService.CanWriteOffice(existing.OfficeCode))
            {
                AddClientConflict(dto, nameof(Item), "Current account cannot modify this office scope.", result);
                continue;
            }

            dto.OfficeCode = _officeScopeService.ResolveScopeForCreate(dto.OfficeCode, existing?.OfficeCode);
            scoped.Add(dto);
        }

        return scoped;
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

            if (!_officeScopeService.CanReadOffice(customer.OfficeCode))
            {
                AddClientConflict(dto, nameof(Invoice),
                    $"Referenced customer is outside the readable office scope: {dto.CustomerId}.", result);
                continue;
            }

            var existing = await _dbContext.Invoices.IgnoreQueryFilters()
                .FirstOrDefaultAsync(x => x.Id == dto.Id, cancellationToken);
            if (existing is not null && !_officeScopeService.CanWriteOffice(existing.OfficeCode))
            {
                AddClientConflict(dto, nameof(Invoice),
                    "Current account cannot modify this office scope.", result);
                continue;
            }

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

            if (existing?.Customer is not null && !_officeScopeService.CanWriteOffice(existing.Customer.OfficeCode))
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

                if (!_officeScopeService.CanWriteOffice(customer.OfficeCode))
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

            if (!_officeScopeService.CanWriteOffice(invoice.OfficeCode))
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
                UpdatedAtUtc = dto.UpdatedAtUtc
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
            if (scopedItem is null || scopedItem.IsDeleted || !_officeScopeService.CanWriteOffice(scopedItem.OfficeCode))
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
            if (item is null || item.IsDeleted || !_officeScopeService.CanWriteOffice(item.OfficeCode) || !_officeScopeService.CanWriteWarehouse(dto.WarehouseCode))
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
                    UpdatedAtUtc = dto.UpdatedAtUtc == default ? DateTime.UtcNow : dto.UpdatedAtUtc
                });
                continue;
            }

            entity.Quantity = dto.Quantity;
            entity.UpdatedAtUtc = dto.UpdatedAtUtc == default ? DateTime.UtcNow : dto.UpdatedAtUtc;
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
        maxRevision = Math.Max(maxRevision, await _dbContext.CustomerMasters.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Customers.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.CustomerContracts.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Items.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Invoices.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Payments.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        return maxRevision;
    }
}
