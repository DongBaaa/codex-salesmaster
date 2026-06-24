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
        var readableCustomerIds = _officeScopeService.ApplyCustomerScope(_dbContext.Customers.AsNoTracking())
            .Select(customer => customer.Id);
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
                (x.Customer != null &&
                 readableCustomerIds.Contains(x.CustomerId) &&
                 x.Customer.NameOriginal.Contains(q)));
        }

        var invoices = await query.OrderByDescending(x => x.InvoiceDate)
            .Take(Math.Min(take, 500))
            .ToListAsync(cancellationToken);
        var readableCustomerIdSet = await LoadReadableCustomerIdSetAsync(
            invoices.Select(invoice => invoice.CustomerId),
            cancellationToken);

        return Ok(invoices
            .Select(invoice => ToScopedDto(invoice, readableCustomerIdSet))
            .ToList());
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
        if (entity is null)
            return NotFound();

        var readableCustomerIdSet = await LoadReadableCustomerIdSetAsync([entity.CustomerId], cancellationToken);
        return Ok(ToScopedDto(entity, readableCustomerIdSet));
    }

    private async Task<HashSet<Guid>> LoadReadableCustomerIdSetAsync(
        IEnumerable<Guid> customerIds,
        CancellationToken cancellationToken)
    {
        var ids = customerIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (ids.Count == 0)
            return [];

        var readableIds = await _officeScopeService.ApplyCustomerScope(_dbContext.Customers.AsNoTracking())
            .Where(customer => ids.Contains(customer.Id))
            .Select(customer => customer.Id)
            .ToListAsync(cancellationToken);
        return readableIds.ToHashSet();
    }

    private static InvoiceDto ToScopedDto(Invoice invoice, IReadOnlySet<Guid> readableCustomerIds)
    {
        var dto = invoice.ToDto();
        if (!readableCustomerIds.Contains(invoice.CustomerId))
            dto.CustomerName = string.Empty;
        return dto;
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
        if (customer is null || customer.IsDeleted)
            return BadRequest("Referenced customer was not found.");
        if (!_officeScopeService.CanWriteOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
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
        dto.Id = entity.Id;
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
        await ProcessedSyncMutationRecorder.RecordAsync(_dbContext, dto, nameof(Invoice), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateRentalSettlementsForInvoiceSaveAsync(null, entity, cancellationToken);
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
        if (!_officeScopeService.CanWriteOfficeForInvoices(entity.ResponsibleOfficeCode, entity.TenantCode, entity.OfficeCode))
            return Forbid();
        if (OptimisticConcurrencyGuard.Check(this, entity, dto, nameof(Invoice)) is { } conflict)
            return conflict;
        if (dto.IsDeleted)
            return SoftDeleteMutationGuard.RejectUpdate("전표");
        if (await ValidateExistingLinkedRentalBillingProfileScopeAsync(entity.LinkedRentalBillingProfileId, cancellationToken) is { } existingRentalProfileScopeError)
            return existingRentalProfileScopeError;

        var previousStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);
        var customer = await _dbContext.Customers
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == dto.CustomerId, cancellationToken);
        if (customer is null || customer.IsDeleted)
            return BadRequest("Referenced customer was not found.");
        if (!_officeScopeService.CanWriteOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
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

        if (await InvoiceStructuralMutationGuard.ShouldProtectExistingInvoiceFromSameIdStructuralMutationAsync(
                _dbContext,
                entity,
                dto,
                cancellationToken,
                protectRentalLinks: false,
                allowSameRentalTargetTransactions: true) &&
            InvoiceStructuralMutationGuard.HasSameIdInvoiceStructuralMutation(entity, dto))
        {
            return Conflict(new ExpectedRevisionConflictResponse
            {
                EntityName = nameof(Invoice),
                EntityId = entity.Id,
                ExpectedRevision = dto.ExpectedRevision > 0 ? dto.ExpectedRevision : dto.Revision,
                CurrentRevision = entity.Revision,
                Reason = ApiConflictReasonTranslator.ProtectedInvoiceSameIdStructuralMutation
            });
        }

        var previousRentalTarget = new Invoice
        {
            LinkedRentalBillingProfileId = entity.LinkedRentalBillingProfileId,
            LinkedRentalBillingRunId = entity.LinkedRentalBillingRunId
        };

        entity.Apply(dto);
        _dbContext.InvoiceLines.RemoveRange(entity.Lines);
        entity.Lines.Clear();
        ApplyInvoiceLines(entity, dto.Lines);
        var currentStockDeltas = await _invoiceStockSnapshotService.BuildInvoiceStockDeltasAsync(entity, cancellationToken);

        await _invoiceStockSnapshotService.ApplyInvoiceStockDeltaDifferenceAsync(
            previousStockDeltas,
            currentStockDeltas,
            cancellationToken);

        await ProcessedSyncMutationRecorder.RecordAsync(_dbContext, dto, nameof(Invoice), cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        await RecalculateRentalSettlementsForInvoiceSaveAsync(previousRentalTarget, entity, cancellationToken);
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
        if (!_officeScopeService.CanWriteOfficeForInvoices(entity.ResponsibleOfficeCode, entity.TenantCode, entity.OfficeCode))
            return Forbid();
        if (OptimisticConcurrencyGuard.Check(this, entity, expectedRevision, nameof(Invoice)) is { } conflict)
            return conflict;
        if (await ValidateInvoiceLineItemScopeAsync(entity.Lines, cancellationToken) is { } lineScopeError)
            return lineScopeError;
        if (await ValidateExistingLinkedRentalBillingProfileScopeAsync(entity.LinkedRentalBillingProfileId, cancellationToken) is { } rentalProfileScopeError)
            return rentalProfileScopeError;
        if (!_currentUserContext.HasPermission(PermissionNames.PaymentEdit) &&
            await HasActivePaymentSideEffectsForInvoiceDeleteAsync([id], cancellationToken))
        {
            return Forbid();
        }
        if (await ValidateLinkedTransactionScopesForInvoiceDeleteAsync([id], cancellationToken) is { } linkedTransactionScopeError)
            return linkedTransactionScopeError;

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

    private async Task RecalculateRentalSettlementsForInvoiceSaveAsync(
        Invoice? previousInvoice,
        Invoice currentInvoice,
        CancellationToken cancellationToken)
    {
        var targets = new List<(Guid ProfileId, Guid? RunId)>();
        AddRentalSettlementTarget(targets, previousInvoice?.LinkedRentalBillingProfileId, previousInvoice?.LinkedRentalBillingRunId);
        AddRentalSettlementTarget(targets, currentInvoice.LinkedRentalBillingProfileId, currentInvoice.LinkedRentalBillingRunId);
        await _rentalSettlementRecalculationService.RecalculateRentalSettlementsAsync(targets.Distinct().ToList(), cancellationToken);
    }

    private async Task<bool> HasActivePaymentSideEffectsForInvoiceDeleteAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return false;

        if (await _dbContext.Payments.IgnoreQueryFilters()
                .AnyAsync(payment => !payment.IsDeleted && invoiceIds.Contains(payment.InvoiceId), cancellationToken))
        {
            return true;
        }

        return await _dbContext.Transactions.IgnoreQueryFilters()
            .AnyAsync(transaction =>
                    !transaction.IsDeleted &&
                    transaction.LinkedInvoiceId.HasValue &&
                    invoiceIds.Contains(transaction.LinkedInvoiceId.Value),
                cancellationToken);
    }

    private static void AddRentalSettlementTarget(List<(Guid ProfileId, Guid? RunId)> targets, Guid? profileId, Guid? runId)
    {
        if (!profileId.HasValue || profileId.Value == Guid.Empty)
            return;

        targets.Add((profileId.Value, runId));
    }

    private async Task<ActionResult?> ValidateLinkedTransactionScopesForInvoiceDeleteAsync(
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return null;

        var linkedTransactions = await _dbContext.Transactions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(transaction =>
                transaction.LinkedInvoiceId.HasValue &&
                invoiceIds.Contains(transaction.LinkedInvoiceId.Value))
            .Select(transaction => new
            {
                transaction.ResponsibleOfficeCode,
                transaction.TenantCode,
                transaction.OfficeCode,
                transaction.LinkedRentalBillingProfileId
            })
            .ToListAsync(cancellationToken);

        foreach (var transaction in linkedTransactions)
        {
            if (!_officeScopeService.CanWriteOfficeForPayments(
                    transaction.ResponsibleOfficeCode,
                    transaction.TenantCode,
                    transaction.OfficeCode))
            {
                return Forbid();
            }
        }

        var profileIds = linkedTransactions
            .Where(transaction =>
                transaction.LinkedRentalBillingProfileId.HasValue &&
                transaction.LinkedRentalBillingProfileId.Value != Guid.Empty)
            .Select(transaction => transaction.LinkedRentalBillingProfileId!.Value)
            .Distinct()
            .ToList();

        foreach (var profileId in profileIds)
        {
            if (await ValidateLinkedRentalBillingProfileScopeAsync(
                    profileId,
                    allowMissingOrDeleted: true,
                    cancellationToken) is { } rentalProfileScopeError)
            {
                return rentalProfileScopeError;
            }
        }

        return null;
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
                return BadRequest($"Referenced invoice line item was not found: {itemId}.");

            if (!_officeScopeService.CanReadOfficeForItems(item.OfficeCode, item.TenantCode))
                return Forbid();
        }

        return null;
    }

    private async Task<ActionResult?> ValidateInvoiceLineItemScopeAsync(
        IEnumerable<InvoiceLine>? lines,
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
                return BadRequest($"Referenced invoice line item was not found: {itemId}.");

            if (!_officeScopeService.CanReadOfficeForItems(item.OfficeCode, item.TenantCode))
                return Forbid();
        }

        return null;
    }

    private async Task<ActionResult?> ValidateLinkedRentalBillingProfileScopeAsync(
        InvoiceDto dto,
        CancellationToken cancellationToken)
        => await ValidateLinkedRentalBillingProfileScopeAsync(
            dto.LinkedRentalBillingProfileId,
            allowMissingOrDeleted: false,
            cancellationToken);

    private async Task<ActionResult?> ValidateExistingLinkedRentalBillingProfileScopeAsync(
        Guid? profileId,
        CancellationToken cancellationToken)
        => await ValidateLinkedRentalBillingProfileScopeAsync(
            profileId,
            allowMissingOrDeleted: true,
            cancellationToken);

    private async Task<ActionResult?> ValidateLinkedRentalBillingProfileScopeAsync(
        Guid? profileId,
        bool allowMissingOrDeleted,
        CancellationToken cancellationToken)
    {
        if (!profileId.HasValue || profileId.Value == Guid.Empty)
            return null;

        var profile = await _dbContext.RentalBillingProfiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(current => current.Id == profileId.Value)
            .Select(current => new
            {
                current.IsDeleted,
                current.ResponsibleOfficeCode,
                current.TenantCode,
                current.OfficeCode
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (profile is null || profile.IsDeleted)
        {
            if (allowMissingOrDeleted)
                return null;

            return BadRequest("Referenced rental billing profile was not found.");
        }

        if (!_officeScopeService.CanWriteOfficeForRentals(profile.ResponsibleOfficeCode, profile.TenantCode, profile.OfficeCode))
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
        entity.OrderIndex = fallbackOrderIndex;
        entity.ItemTrackingType = ItemTrackingTypes.Normalize(line.ItemTrackingType);
        entity.IsDeleted = line.IsDeleted;
    }
}
