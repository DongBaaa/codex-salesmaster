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
    private static readonly JsonSerializerOptions ConflictJsonOptions = new() { WriteIndented = false };

    private readonly AppDbContext _dbContext;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IInvoiceNumberService _invoiceNumberService;

    public SyncController(AppDbContext dbContext, ICurrentUserContext currentUserContext, IInvoiceNumberService invoiceNumberService)
    {
        _dbContext = dbContext;
        _currentUserContext = currentUserContext;
        _invoiceNumberService = invoiceNumberService;
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
            CustomerMasters = await _dbContext.CustomerMasters.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Customers = await _dbContext.Customers.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Items = await _dbContext.Items.IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.Revision > sinceRev).Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Invoices = await _dbContext.Invoices.IgnoreQueryFilters().Include(x => x.Lines).AsNoTracking()
                .Where(x => x.Revision > sinceRev).OrderBy(x => x.CreatedAtUtc)
                .Select(x => x.ToDto()).ToListAsync(cancellationToken),
            Payments = await _dbContext.Payments.IgnoreQueryFilters().AsNoTracking()
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
        var validCustomerMasters = await FilterValidCustomerMastersAsync(request.CustomerMasters ?? [], result, cancellationToken);
        await UpsertEntitiesAsync(validCustomerMasters, _dbContext.CustomerMasters,
            (e, d) => e.Apply(d), d => new CustomerMaster { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        var validCustomers = await FilterValidCustomersAsync(request.Customers ?? [], result, cancellationToken);
        await UpsertEntitiesAsync(validCustomers, _dbContext.Customers,
            (e, d) => e.Apply(d), d => new Customer { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
        await UpsertEntitiesAsync(request.Items ?? [], _dbContext.Items,
            (e, d) => e.Apply(d), d => new Item { Id = d.Id == Guid.Empty ? Guid.NewGuid() : d.Id }, result, cancellationToken);
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

            if (dto.CustomerMasterId.HasValue &&
                !await ExistsOrTrackedAsync(_dbContext.CustomerMasters, dto.CustomerMasterId.Value, cancellationToken))
            {
                AddClientConflict(dto, nameof(Customer),
                    $"Referenced customer master was not found: {dto.CustomerMasterId}.", result);
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
            if (dto.CustomerId == Guid.Empty ||
                !await ExistsOrTrackedAsync(_dbContext.Customers, dto.CustomerId, cancellationToken))
            {
                AddClientConflict(dto, nameof(Invoice),
                    $"Referenced customer was not found: {dto.CustomerId}.", result);
                continue;
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
            if (dto.InvoiceId == Guid.Empty ||
                !await ExistsOrTrackedAsync(_dbContext.Invoices, dto.InvoiceId, cancellationToken))
            {
                AddClientConflict(dto, nameof(Payment),
                    $"Referenced invoice was not found: {dto.InvoiceId}.", result);
                continue;
            }

            valid.Add(dto);
        }

        return valid;
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
            UserId = _currentUserContext.UserId, Username = _currentUserContext.Username,
            EntityName = entityName,
            EntityId = server switch { TrackedEntity tracked => tracked.Id.ToString(), _ => string.Empty },
            ClientJson = JsonSerializer.Serialize(client, ConflictJsonOptions),
            ServerJson = JsonSerializer.Serialize(server, ConflictJsonOptions),
            Reason = reason, CreatedAtUtc = DateTime.UtcNow
        };
    }

    private async Task<long> GetCurrentRevisionAsync(CancellationToken cancellationToken)
    {
        var maxRevision = 0L;
        maxRevision = Math.Max(maxRevision, await _dbContext.CompanyProfiles.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Units.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.CustomerCategories.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.CustomerMasters.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Customers.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Items.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Invoices.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        maxRevision = Math.Max(maxRevision, await _dbContext.Payments.IgnoreQueryFilters().Select(x => (long?)x.Revision).MaxAsync(cancellationToken) ?? 0);
        return maxRevision;
    }
}
