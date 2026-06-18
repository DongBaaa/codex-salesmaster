using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Mappings;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Controllers;

[ApiController]
[Authorize]
[Route("invoices")]
public sealed class InvoicesController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ICurrentUserContext _currentUserContext;
    private readonly IInvoiceNumberService _invoiceNumberService;
    private readonly OfficeScopeService _officeScopeService;
    private readonly InventoryLedgerService _inventoryLedgerService;
    private readonly InvoiceStockSnapshotService _invoiceStockSnapshotService;
    private readonly RentalSettlementRecalculationService _rentalSettlementRecalculationService;

    public InvoicesController(
        AppDbContext dbContext,
        ICurrentUserContext currentUserContext,
        IInvoiceNumberService invoiceNumberService,
        OfficeScopeService officeScopeService,
        InventoryLedgerService inventoryLedgerService,
        InvoiceStockSnapshotService invoiceStockSnapshotService,
        RentalSettlementRecalculationService rentalSettlementRecalculationService)
    {
        _dbContext = dbContext;
        _currentUserContext = currentUserContext;
        _invoiceNumberService = invoiceNumberService;
        _officeScopeService = officeScopeService;
        _inventoryLedgerService = inventoryLedgerService;
        _invoiceStockSnapshotService = invoiceStockSnapshotService;
        _rentalSettlementRecalculationService = rentalSettlementRecalculationService;
    }

    [HttpGet]
    public async Task<ActionResult<List<InvoiceDto>>> GetAll(
        [FromQuery] Guid? customerId,
        [FromQuery] string? q,
        [FromQuery] int take = 200,
        CancellationToken cancellationToken = default)
    {
        var query = _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices
            .Include(x => x.Customer)
            .Include(x => x.Lines)
            .Include(x => x.Payments)
            .ThenInclude(payment => payment.Attachments)
            .AsNoTracking());
        if (customerId.HasValue) query = query.Where(x => x.CustomerId == customerId.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            query = query.Where(x =>
                x.InvoiceNumber.Contains(q) ||
                x.Memo.Contains(q) ||
                (x.Customer != null && x.Customer.NameOriginal.Contains(q)));
        }

        return Ok(await query.OrderByDescending(x => x.InvoiceDate)
            .Take(Math.Min(take, 500)).Select(x => x.ToDto()).ToListAsync(cancellationToken));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InvoiceDto>> GetById(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _officeScopeService.ApplyInvoiceScope(_dbContext.Invoices
            .Include(x => x.Customer)
            .Include(x => x.Lines)
            .Include(x => x.Payments)
            .ThenInclude(payment => payment.Attachments)
            .AsNoTracking())
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? NotFound() : Ok(entity.ToDto());
    }

    [HttpPost]
    [Authorize(Policy = PermissionNames.InvoiceEdit)]
    public async Task<ActionResult<InvoiceDto>> Create([FromBody] InvoiceDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditInvoices())
            return Forbid();

        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
        if (customer is null)
            return BadRequest("Referenced customer was not found.");
        if (!_officeScopeService.CanReadOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode))
            return Forbid();

        dto.ResponsibleOfficeCode = _officeScopeService.ResolveInvoiceResponsibleScopeForCreate(
            dto.ResponsibleOfficeCode,
            customer.ResponsibleOfficeCode);
        dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            dto.OfficeCode,
            dto.ResponsibleOfficeCode,
            customer.OfficeCode);
        dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
            dto.TenantCode,
            dto.OfficeCode,
            customer.TenantCode,
            customer.OfficeCode);
        if (await ValidateInvoiceLineItemScopeAsync(dto.Lines, cancellationToken) is { } lineScopeError)
            return lineScopeError;
        if (await ValidateLinkedRentalBillingProfileScopeAsync(dto, cancellationToken) is { } rentalProfileScopeError)
            return rentalProfileScopeError;

        var entity = new Invoice { Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id };
        entity.Apply(dto);
        if (string.IsNullOrWhiteSpace(entity.InvoiceNumber))
        {
            entity.InvoiceNumber = await _invoiceNumberService.GenerateAsync(entity.CustomerId, entity.InvoiceDate, cancellationToken);
        }

        ApplyInvoiceLines(entity, dto.Lines);
        var currentStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);

        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
            currentStockDeltas,
            cancellationToken);

        _dbContext.Invoices.Add(entity);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = PermissionNames.InvoiceEdit)]
    public async Task<ActionResult<InvoiceDto>> Update(Guid id, [FromBody] InvoiceDto dto, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditInvoices())
            return Forbid();

        var entity = await _dbContext.Invoices.Include(x => x.Customer).Include(x => x.Lines)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!_officeScopeService.CanWriteOfficeForInvoices(entity.ResponsibleOfficeCode, entity.TenantCode))
            return Forbid();
        if (OptimisticConcurrencyGuard.Check(this, entity, dto, nameof(Invoice)) is { } conflict)
            return conflict;

        var previousStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);
        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
        if (customer is null)
            return BadRequest("Referenced customer was not found.");
        if (!_officeScopeService.CanReadOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode))
            return Forbid();

        dto.ResponsibleOfficeCode = _officeScopeService.ResolveInvoiceResponsibleScopeForCreate(
            dto.ResponsibleOfficeCode,
            customer.ResponsibleOfficeCode);
        dto.OfficeCode = _officeScopeService.ResolveOwningOfficeForOperationalScope(
            dto.OfficeCode,
            dto.ResponsibleOfficeCode,
            customer.OfficeCode);
        dto.TenantCode = _officeScopeService.ResolveTenantForCreate(
            dto.TenantCode,
            dto.OfficeCode,
            customer.TenantCode,
            customer.OfficeCode);
        if (await ValidateInvoiceLineItemScopeAsync(dto.Lines, cancellationToken) is { } lineScopeError)
            return lineScopeError;
        if (await ValidateLinkedRentalBillingProfileScopeAsync(dto, cancellationToken) is { } rentalProfileScopeError)
            return rentalProfileScopeError;

        entity.Apply(dto);
        _dbContext.InvoiceLines.RemoveRange(entity.Lines);
        entity.Lines.Clear();
        ApplyInvoiceLines(entity, dto.Lines);
        var currentStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);

        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            previousStockDeltas,
            currentStockDeltas,
            cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        return Ok(entity.ToDto());
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = PermissionNames.InvoiceEdit)]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] long? expectedRevision, CancellationToken cancellationToken)
    {
        if (!_officeScopeService.CanEditInvoices())
            return Forbid();

        var entity = await _dbContext.Invoices.Include(x => x.Customer).Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity is null) return NotFound();
        if (!_officeScopeService.CanWriteOfficeForInvoices(entity.ResponsibleOfficeCode, entity.TenantCode))
            return Forbid();
        if (OptimisticConcurrencyGuard.Check(this, entity, expectedRevision, nameof(Invoice)) is { } conflict)
            return conflict;

        var rentalSettlementTargets = await _rentalSettlementRecalculationService.LoadRentalSettlementTargetsForInvoiceDeleteAsync([id], cancellationToken);
        var previousStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);
        entity.IsDeleted = true;
        foreach (var line in entity.Lines)
        {
            line.IsDeleted = true;
        }
        await _rentalSettlementRecalculationService.DetachTransactionsFromInvoicesAsync([id], cancellationToken);
        await _rentalSettlementRecalculationService.MarkPaymentsDeletedForInvoicesAsync([id], cancellationToken);

        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            previousStockDeltas,
            new Dictionary<InvoiceStockSnapshotService.InvoiceStockKey, decimal>(),
            cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _rentalSettlementRecalculationService.RecalculateRentalSettlementsAsync(rentalSettlementTargets, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await _inventoryLedgerService.RebuildAsync(cancellationToken);
        return NoContent();
    }

    private async Task<ActionResult?> ValidateInvoiceLineItemScopeAsync(
        IEnumerable<InvoiceLineDto>? lines,
        CancellationToken cancellationToken)
    {
        var itemIds = (lines ?? [])
            .Where(line => !line.IsDeleted && line.ItemId.HasValue && line.ItemId.Value != Guid.Empty)
            .Select(line => line.ItemId!.Value)
            .Distinct()
            .ToList();
        if (itemIds.Count == 0)
            return null;

        var items = await _dbContext.Items
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(item => itemIds.Contains(item.Id) && !item.IsDeleted)
            .Select(item => new { item.Id, item.OfficeCode, item.TenantCode })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        foreach (var itemId in itemIds)
        {
            if (!items.TryGetValue(itemId, out var item))
                continue;

            if (!_officeScopeService.CanReadOfficeForItems(item.OfficeCode, item.TenantCode))
                return Forbid();
        }

        return null;
    }

    private async Task<ActionResult?> ValidateLinkedRentalBillingProfileScopeAsync(
        InvoiceDto dto,
        CancellationToken cancellationToken)
    {
        if (!dto.LinkedRentalBillingProfileId.HasValue || dto.LinkedRentalBillingProfileId.Value == Guid.Empty)
            return null;

        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id == dto.LinkedRentalBillingProfileId.Value)
            .Select(current => new
            {
                current.IsDeleted,
                current.ResponsibleOfficeCode,
                current.TenantCode
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (profile is null || profile.IsDeleted)
            return BadRequest("Referenced rental billing profile was not found.");

        if (!_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode))
            return Forbid();

        return null;
    }

    private static void ApplyInvoiceLines(Invoice invoice, IEnumerable<InvoiceLineDto>? lines)
    {
        if (invoice.IsDeleted)
            return;

        var order = 1;
        foreach (var line in lines ?? [])
        {
            if (line.IsDeleted)
                continue;

            invoice.Lines.Add(CreateInvoiceLine(invoice.Id, line, line.Id == Guid.Empty ? Guid.NewGuid() : line.Id, order++));
        }
    }

    private static InvoiceLine CreateInvoiceLine(Guid invoiceId, InvoiceLineDto line, Guid resolvedId, int fallbackOrderIndex)
    {
        var entity = new InvoiceLine();
        ApplyInvoiceLine(entity, invoiceId, line, resolvedId, fallbackOrderIndex);
        return entity;
    }

    private static void ApplyInvoiceLine(InvoiceLine entity, Guid invoiceId, InvoiceLineDto line, Guid resolvedId, int fallbackOrderIndex)
    {
        entity.Id = resolvedId;
        entity.InvoiceId = invoiceId;
        entity.ItemId = line.ItemId;
        entity.ItemNameOriginal = line.ItemNameOriginal;
        entity.SpecificationOriginal = line.SpecificationOriginal;
        entity.Unit = line.Unit;
        entity.Quantity = line.Quantity;
        entity.UnitPrice = line.UnitPrice;
        entity.LineAmount = line.LineAmount == 0 ? line.Quantity * line.UnitPrice : line.LineAmount;
        entity.Remark = line.Remark;
        entity.SerialNumber = line.SerialNumber;
        entity.MaterialNumber = line.MaterialNumber;
        entity.InstallLocation = line.InstallLocation;
        entity.RentalStartDate = line.RentalStartDate;
        entity.RentalEndDate = line.RentalEndDate;
        entity.OrderIndex = line.OrderIndex > 0 ? line.OrderIndex : fallbackOrderIndex;
        entity.ItemTrackingType = ItemTrackingTypes.Normalize(line.ItemTrackingType);
        entity.IsDeleted = line.IsDeleted;
    }
}
